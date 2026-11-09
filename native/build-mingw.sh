#!/usr/bin/env bash
# Linux'ta mingw-w64 ile hook DLL + injector'ı (x64 ve x86) çapraz derler → native/bin
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p bin
for arch in x86_64 i686; do
  bits=$([ "$arch" = x86_64 ] && echo 64 || echo 32)
  tc="build-$bits/toolchain.cmake"
  mkdir -p "build-$bits"
  cat > "$tc" <<EOF
set(CMAKE_SYSTEM_NAME Windows)
set(CMAKE_C_COMPILER ${arch}-w64-mingw32-gcc-posix)
set(CMAKE_CXX_COMPILER ${arch}-w64-mingw32-g++-posix)
set(CMAKE_RC_COMPILER ${arch}-w64-mingw32-windres)
set(CMAKE_FIND_ROOT_PATH /usr/${arch}-w64-mingw32)
EOF
  cmake -S . -B "build-$bits" -DCMAKE_TOOLCHAIN_FILE="$tc" -DCMAKE_BUILD_TYPE=Release >/dev/null
  cmake --build "build-$bits" -j"$(nproc)"
  cp "build-$bits"/GameSubTR.Hook$bits.dll "build-$bits"/GameSubTR.Injector$bits.exe bin/
done
ls -la bin
