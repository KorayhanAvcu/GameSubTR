// Direct3D 9 Present/Reset hook (eski exclusive fullscreen oyunların çoğu DX9'dur).
#include <d3d9.h>
#include "MinHook.h"
#include "imgui.h"
#include "imgui_impl_dx9.h"
#include "common.h"

namespace gstr {

using Dx9PresentFn = HRESULT(STDMETHODCALLTYPE*)(IDirect3DDevice9*, const RECT*, const RECT*, HWND, const RGNDATA*);
using Dx9ResetFn = HRESULT(STDMETHODCALLTYPE*)(IDirect3DDevice9*, D3DPRESENT_PARAMETERS*);

static Dx9PresentFn oPresent9 = nullptr;
static Dx9ResetFn oReset9 = nullptr;
static IDirect3DDevice9* g_dev9 = nullptr;
static bool g_ready9 = false;
static bool g_skip9 = false;
static IDirect3DSurface9* g_sysmem = nullptr;
static IDirect3DSurface9* g_resolve9 = nullptr;
static D3DSURFACE_DESC g_sysDesc = {};

template <class T> static void Rel(T*& p) { if (p) { p->Release(); p = nullptr; } }

static void ReleaseCapture9() { Rel(g_sysmem); Rel(g_resolve9); g_sysDesc = {}; }

static PixFmt ToPixFmt9(D3DFORMAT f) {
    switch (f) {
    case D3DFMT_A8R8G8B8: case D3DFMT_X8R8G8B8: return PixFmt::BGRA8;
    case D3DFMT_A8B8G8R8: case D3DFMT_X8B8G8R8: return PixFmt::RGBA8;
    case D3DFMT_A2B10G10R10: return PixFmt::RGB10A2;
    default: return PixFmt::Unsupported;
    }
}

static void DoCapture9(IDirect3DDevice9* dev, IDirect3DSurface9* bb, const D3DSURFACE_DESC& d) {
    uint32_t x, y, w, h, seq;
    if (!PendingCapture(d.Width, d.Height, x, y, w, h, seq)) return;
    PixFmt pf = ToPixFmt9(d.Format);
    if (pf == PixFmt::Unsupported) { CompleteCapture(seq, 0, 0, 1); return; }

    if (!g_sysmem || g_sysDesc.Width != d.Width || g_sysDesc.Height != d.Height || g_sysDesc.Format != d.Format) {
        ReleaseCapture9();
        if (FAILED(dev->CreateOffscreenPlainSurface(d.Width, d.Height, d.Format, D3DPOOL_SYSTEMMEM, &g_sysmem, nullptr))) {
            CompleteCapture(seq, 0, 0, 2); return;
        }
        g_sysDesc = d;
    }
    IDirect3DSurface9* src = bb;
    if (d.MultiSampleType != D3DMULTISAMPLE_NONE) {
        if (!g_resolve9 && FAILED(dev->CreateRenderTarget(d.Width, d.Height, d.Format, D3DMULTISAMPLE_NONE, 0, FALSE, &g_resolve9, nullptr))) {
            CompleteCapture(seq, 0, 0, 2); return;
        }
        if (FAILED(dev->StretchRect(bb, nullptr, g_resolve9, nullptr, D3DTEXF_NONE))) { CompleteCapture(seq, 0, 0, 2); return; }
        src = g_resolve9;
    }
    if (FAILED(dev->GetRenderTargetData(src, g_sysmem))) { CompleteCapture(seq, 0, 0, 2); return; }
    RECT rc = { (LONG)x, (LONG)y, (LONG)(x + w), (LONG)(y + h) };
    D3DLOCKED_RECT lr = {};
    if (FAILED(g_sysmem->LockRect(&lr, &rc, D3DLOCK_READONLY))) { CompleteCapture(seq, 0, 0, 2); return; }
    for (uint32_t r = 0; r < h; r++)
        ConvertRow(pf, (const uint8_t*)lr.pBits + (size_t)r * lr.Pitch, g_shared->pixels + (size_t)r * w * 4, w);
    g_sysmem->UnlockRect();
    CompleteCapture(seq, w, h, 0);
}

static void OnPresent9(IDirect3DDevice9* dev) {
    if (g_skip9 || !g_shared) return;
    if (dev != g_dev9) {
        if (!ClaimApi(Api::DX9)) { g_skip9 = true; return; }
        if (g_ready9) { ImGui_ImplDX9_Shutdown(); g_ready9 = false; }
        ReleaseCapture9();
        g_dev9 = dev;
        Logf("D3D9 cihazı bulundu");
    }
    IDirect3DSurface9* bb = nullptr;
    if (FAILED(dev->GetBackBuffer(0, 0, D3DBACKBUFFER_TYPE_MONO, &bb)) || !bb) return;
    D3DSURFACE_DESC d;
    bb->GetDesc(&d);
    Heartbeat(Api::DX9, d.Width, d.Height);
    DoCapture9(dev, bb, d);

    const SubtitleState& s = PollSubtitle();
    if (s.visible && !s.text.empty()) {
        if (!g_ready9) { ImGuiInitCommon(); g_ready9 = ImGui_ImplDX9_Init(dev); }
        if (g_ready9 && SUCCEEDED(dev->BeginScene())) {
            IDirect3DSurface9* oldRt = nullptr;
            dev->GetRenderTarget(0, &oldRt);
            dev->SetRenderTarget(0, bb);
            ImGui::GetIO().DisplaySize = ImVec2((float)d.Width, (float)d.Height);
            ImGui::GetIO().DeltaTime = 1.f / 60.f;
            ImGui_ImplDX9_NewFrame();
            ImGui::NewFrame();
            DrawSubtitle((float)d.Width, (float)d.Height);
            ImGui::EndFrame();
            ImGui::Render();
            ImGui_ImplDX9_RenderDrawData(ImGui::GetDrawData());
            if (oldRt) { dev->SetRenderTarget(0, oldRt); oldRt->Release(); }
            dev->EndScene();
        }
    }
    bb->Release();
}

static HRESULT STDMETHODCALLTYPE hkPresent9(IDirect3DDevice9* dev, const RECT* a, const RECT* b, HWND w, const RGNDATA* r) {
    OnPresent9(dev);
    return oPresent9(dev, a, b, w, r);
}

static HRESULT STDMETHODCALLTYPE hkReset9(IDirect3DDevice9* dev, D3DPRESENT_PARAMETERS* pp) {
    ReleaseCapture9();
    if (g_ready9 && dev == g_dev9) ImGui_ImplDX9_InvalidateDeviceObjects();
    HRESULT hr = oReset9(dev, pp);
    if (g_ready9 && dev == g_dev9 && SUCCEEDED(hr)) ImGui_ImplDX9_CreateDeviceObjects();
    return hr;
}

static LRESULT CALLBACK DummyWndProc9(HWND h, UINT m, WPARAM w, LPARAM l) { return DefWindowProcW(h, m, w, l); }

bool InstallDx9Hooks() {
    using Create9Fn = IDirect3D9*(WINAPI*)(UINT);
    auto create9 = reinterpret_cast<Create9Fn>(GetProcAddress(GetModuleHandleW(L"d3d9.dll"), "Direct3DCreate9"));
    if (!create9) return false;
    IDirect3D9* d3d = create9(D3D_SDK_VERSION);
    if (!d3d) return false;
    WNDCLASSEXW wc = { sizeof(wc), CS_CLASSDC, DummyWndProc9, 0, 0, GetModuleHandleW(nullptr), nullptr, nullptr, nullptr, nullptr, L"GameSubTR_DX9Dummy", nullptr };
    RegisterClassExW(&wc);
    HWND hwnd = CreateWindowW(wc.lpszClassName, L"", WS_OVERLAPPEDWINDOW, 0, 0, 100, 100, nullptr, nullptr, wc.hInstance, nullptr);
    D3DPRESENT_PARAMETERS pp = {};
    pp.Windowed = TRUE;
    pp.SwapEffect = D3DSWAPEFFECT_DISCARD;
    pp.hDeviceWindow = hwnd;
    pp.BackBufferFormat = D3DFMT_UNKNOWN;
    IDirect3DDevice9* dev = nullptr;
    HRESULT hr = d3d->CreateDevice(D3DADAPTER_DEFAULT, D3DDEVTYPE_HAL, hwnd,
        D3DCREATE_SOFTWARE_VERTEXPROCESSING | D3DCREATE_DISABLE_DRIVER_MANAGEMENT, &pp, &dev);
    if (FAILED(hr))
        hr = d3d->CreateDevice(D3DADAPTER_DEFAULT, D3DDEVTYPE_NULLREF, hwnd, D3DCREATE_SOFTWARE_VERTEXPROCESSING, &pp, &dev);
    bool ok = false;
    if (SUCCEEDED(hr) && dev) {
        void** vt = *reinterpret_cast<void***>(dev);
        ok = MH_CreateHook(vt[17], (void*)&hkPresent9, (void**)&oPresent9) == MH_OK &&
             MH_CreateHook(vt[16], (void*)&hkReset9, (void**)&oReset9) == MH_OK &&
             MH_EnableHook(MH_ALL_HOOKS) == MH_OK;
        dev->Release();
    }
    d3d->Release();
    DestroyWindow(hwnd);
    UnregisterClassW(wc.lpszClassName, wc.hInstance);
    Logf("DX9 hook kurulumu: %s", ok ? "OK" : "BAŞARISIZ");
    return ok;
}

} // namespace gstr
