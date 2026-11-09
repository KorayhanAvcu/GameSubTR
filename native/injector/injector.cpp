// GameSubTR.Injector32/64.exe <pid> <dll yolu>
// Hedef süreçle aynı bitlikte derlenmelidir (LoadLibraryW adresi bu sayede geçerlidir).
#include <windows.h>
#include <cstdio>
#include <cwchar>
#include <cstdlib>

int wmain(int argc, wchar_t** argv) {
    if (argc < 3) { fwprintf(stderr, L"kullanım: injector <pid> <dll>\n"); return 2; }
    DWORD pid = (DWORD)wcstoul(argv[1], nullptr, 10);
    const wchar_t* dll = argv[2];
    if (GetFileAttributesW(dll) == INVALID_FILE_ATTRIBUTES) { fwprintf(stderr, L"DLL bulunamadı\n"); return 3; }

    HANDLE proc = OpenProcess(PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION |
                              PROCESS_VM_WRITE | PROCESS_VM_READ, FALSE, pid);
    if (!proc) { fwprintf(stderr, L"OpenProcess başarısız (%lu). Oyun yönetici olarak çalışıyorsa GameSubTR'yi de yönetici olarak açın.\n", GetLastError()); return 4; }

    BOOL selfWow = FALSE, targetWow = FALSE;
    IsWow64Process(GetCurrentProcess(), &selfWow);
    IsWow64Process(proc, &targetWow);
    if (selfWow != targetWow) { fwprintf(stderr, L"Bitlik uyuşmuyor\n"); CloseHandle(proc); return 5; }

    size_t bytes = (wcslen(dll) + 1) * sizeof(wchar_t);
    void* remote = VirtualAllocEx(proc, nullptr, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!remote || !WriteProcessMemory(proc, remote, dll, bytes, nullptr)) {
        fwprintf(stderr, L"Bellek yazılamadı (%lu)\n", GetLastError());
        CloseHandle(proc); return 6;
    }
    auto loadLib = (LPTHREAD_START_ROUTINE)GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "LoadLibraryW");
    HANDLE th = CreateRemoteThread(proc, nullptr, 0, loadLib, remote, 0, nullptr);
    if (!th) { fwprintf(stderr, L"CreateRemoteThread başarısız (%lu)\n", GetLastError()); VirtualFreeEx(proc, remote, 0, MEM_RELEASE); CloseHandle(proc); return 7; }
    DWORD wait = WaitForSingleObject(th, 15000);
    DWORD code = 0;
    GetExitCodeThread(th, &code);
    CloseHandle(th);
    VirtualFreeEx(proc, remote, 0, MEM_RELEASE);
    CloseHandle(proc);
    if (wait != WAIT_OBJECT_0) { fwprintf(stderr, L"LoadLibrary zaman aşımı\n"); return 8; }
    if (code == 0) { fwprintf(stderr, L"LoadLibrary başarısız (DLL yüklenemedi)\n"); return 9; }
    wprintf(L"OK\n");
    return 0;
}
