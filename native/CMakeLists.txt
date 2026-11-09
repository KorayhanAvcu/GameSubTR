cmake_minimum_required(VERSION 3.20)
project(GameSubTRNative CXX C)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Bitlik soneki (32/64)
if(CMAKE_SIZEOF_VOID_P EQUAL 8)
  set(BITS 64)
else()
  set(BITS 32)
endif()

# Üçüncü parti kaynaklar: native/third_party varsa onu, yoksa GitHub'dan indir.
set(TP ${CMAKE_CURRENT_SOURCE_DIR}/third_party)
if(NOT EXISTS ${TP}/minhook/src/hook.c OR NOT EXISTS ${TP}/imgui/imgui.cpp)
  include(FetchContent)
  FetchContent_Declare(minhook GIT_REPOSITORY https://github.com/TsudaKageyu/minhook.git GIT_TAG master GIT_SHALLOW TRUE)
  FetchContent_Declare(imgui GIT_REPOSITORY https://github.com/ocornut/imgui.git GIT_TAG v1.91.9 GIT_SHALLOW TRUE)
  FetchContent_Populate(minhook)
  FetchContent_Populate(imgui)
  set(MINHOOK_DIR ${minhook_SOURCE_DIR})
  set(IMGUI_DIR ${imgui_SOURCE_DIR})
else()
  set(MINHOOK_DIR ${TP}/minhook)
  set(IMGUI_DIR ${TP}/imgui)
endif()

set(MINHOOK_SRC
  ${MINHOOK_DIR}/src/buffer.c
  ${MINHOOK_DIR}/src/hook.c
  ${MINHOOK_DIR}/src/trampoline.c
  ${MINHOOK_DIR}/src/hde/hde32.c
  ${MINHOOK_DIR}/src/hde/hde64.c)

set(IMGUI_SRC
  ${IMGUI_DIR}/imgui.cpp
  ${IMGUI_DIR}/imgui_draw.cpp
  ${IMGUI_DIR}/imgui_tables.cpp
  ${IMGUI_DIR}/imgui_widgets.cpp
  ${IMGUI_DIR}/backends/imgui_impl_dx9.cpp
  ${IMGUI_DIR}/backends/imgui_impl_dx11.cpp)

add_library(GameSubTR.Hook SHARED
  hook/dllmain.cpp hook/common.cpp hook/dx11.cpp hook/dx9.cpp
  ${MINHOOK_SRC} ${IMGUI_SRC})
set_target_properties(GameSubTR.Hook PROPERTIES OUTPUT_NAME "GameSubTR.Hook${BITS}" PREFIX "")
target_include_directories(GameSubTR.Hook PRIVATE hook ${MINHOOK_DIR}/include ${IMGUI_DIR} ${IMGUI_DIR}/backends)
target_compile_definitions(GameSubTR.Hook PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX IMGUI_DISABLE_DEMO_WINDOWS IMGUI_DISABLE_DEBUG_TOOLS UNICODE _UNICODE)
# d3d9/d3d11 bilinçli olarak linklenmez (dinamik yüklenir); d3dcompiler ImGui DX11 arka ucu için.
target_link_libraries(GameSubTR.Hook PRIVATE d3dcompiler)

add_executable(GameSubTR.Injector injector/injector.cpp)
set_target_properties(GameSubTR.Injector PROPERTIES OUTPUT_NAME "GameSubTR.Injector${BITS}")
target_compile_definitions(GameSubTR.Injector PRIVATE UNICODE _UNICODE)

if(MSVC)
  # Statik CRT: oyun bilgisayarında VC++ runtime gerekmesin.
  set_property(TARGET GameSubTR.Hook GameSubTR.Injector PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
  target_compile_options(GameSubTR.Hook PRIVATE /utf-8 /W3)
  target_compile_options(GameSubTR.Injector PRIVATE /utf-8)
else()
  target_link_options(GameSubTR.Hook PRIVATE -static -static-libgcc -static-libstdc++ -Wl,--exclude-all-symbols)
  target_link_options(GameSubTR.Injector PRIVATE -static -static-libgcc -static-libstdc++ -municode)
endif()
