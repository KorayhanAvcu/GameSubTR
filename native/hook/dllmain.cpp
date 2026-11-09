// GameSubTR hook DLL giriş noktası.
// Oyuna yüklendikten sonra paylaşımlı belleği açar ve yüklü grafik API'lerine hook kurar.
#include <windows.h>
#include <cwchar>
#include "MinHook.h"
#include "common.h"

namespace gstr {
bool InstallDx11Hooks();
bool InstallDx9Hooks();
}

using namespace gstr;

static DWORD WINAPI MainThread(LPVOID) {
    if (MH_Initialize() != MH_OK) { Logf("MinHook başlatılamadı"); return 1; }

    wchar_t name[64];
    swprintf(name, 64, L"Local\\GameSubTR_%lu", GetCurrentProcessId());
    HANDLE map = nullptr;
    for (int i = 0; i < 100 && !map; i++) {
        map = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, name);
        if (!map) Sleep(100);
    }
    if (!map) { Logf("Paylaşımlı bellek bulunamadı"); return 2; }
    g_shared = static_cast<Shared*>(MapViewOfFile(map, FILE_MAP_ALL_ACCESS, 0, 0, kTotalSize));
    if (!g_shared || g_shared->magic != kMagic) { Logf("Paylaşımlı bellek geçersiz"); return 3; }

    // Grafik DLL'leri oyun tarafından sonradan yüklenebilir; 60 sn boyunca izle.
    bool dx11 = false, dx9 = false;
    for (int i = 0; i < 120 && !(dx11 && dx9); i++) {
        if (!dx11 && GetModuleHandleW(L"d3d11.dll") && GetModuleHandleW(L"dxgi.dll")) dx11 = InstallDx11Hooks();
        if (!dx9 && GetModuleHandleW(L"d3d9.dll")) dx9 = InstallDx9Hooks();
        if ((dx11 || dx9) && i > 10) break; // biri kuruldu, kısa süre diğerini de bekledik
        Sleep(500);
    }
    if (!dx11 && !dx9) Logf("Desteklenen grafik API'si bulunamadı");
    return 0;
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(module);
        // Yükleyici kilidi altında ağır iş yapma; ayrı iş parçacığı.
        HANDLE t = CreateThread(nullptr, 0, MainThread, nullptr, 0, nullptr);
        if (t) CloseHandle(t);
    }
    return TRUE;
}
