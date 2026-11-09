// DXGI/D3D11 Present hook: altyazıyı oyunun kendi arka tamponuna çizer ve
// istenen bölgeyi arka tampondan okur (exclusive fullscreen'de de çalışır).
#include <d3d11.h>
#include <dxgi1_2.h>
#include "MinHook.h"
#include "imgui.h"
#include "imgui_impl_dx11.h"
#include "common.h"

namespace gstr {

using PresentFn = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain*, UINT, UINT);
using Present1Fn = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain1*, UINT, UINT, const DXGI_PRESENT_PARAMETERS*);
using ResizeBuffersFn = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain*, UINT, UINT, UINT, DXGI_FORMAT, UINT);

static PresentFn oPresent = nullptr;
static Present1Fn oPresent1 = nullptr;
static ResizeBuffersFn oResizeBuffers = nullptr;

static ID3D11Device* g_dev = nullptr;
static ID3D11DeviceContext* g_ctx = nullptr;
static IDXGISwapChain* g_sc = nullptr;
static ID3D11RenderTargetView* g_rtv = nullptr;
static bool g_unsupported = false;
static bool g_imguiReady = false;

static ID3D11Texture2D* g_staging = nullptr;
static D3D11_TEXTURE2D_DESC g_stagingDesc = {};
static ID3D11Texture2D* g_resolve = nullptr;

template <class T> static void SafeRelease(T*& p) { if (p) { p->Release(); p = nullptr; } }

static DXGI_FORMAT TypedFormat(DXGI_FORMAT f) {
    switch (f) {
    case DXGI_FORMAT_R8G8B8A8_TYPELESS: return DXGI_FORMAT_R8G8B8A8_UNORM;
    case DXGI_FORMAT_B8G8R8A8_TYPELESS: return DXGI_FORMAT_B8G8R8A8_UNORM;
    case DXGI_FORMAT_B8G8R8X8_TYPELESS: return DXGI_FORMAT_B8G8R8X8_UNORM;
    case DXGI_FORMAT_R10G10B10A2_TYPELESS: return DXGI_FORMAT_R10G10B10A2_UNORM;
    case DXGI_FORMAT_R16G16B16A16_TYPELESS: return DXGI_FORMAT_R16G16B16A16_FLOAT;
    default: return f;
    }
}

static PixFmt ToPixFmt(DXGI_FORMAT f) {
    switch (f) {
    case DXGI_FORMAT_B8G8R8A8_UNORM: case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB: case DXGI_FORMAT_B8G8R8A8_TYPELESS:
    case DXGI_FORMAT_B8G8R8X8_UNORM: case DXGI_FORMAT_B8G8R8X8_UNORM_SRGB: case DXGI_FORMAT_B8G8R8X8_TYPELESS:
        return PixFmt::BGRA8;
    case DXGI_FORMAT_R8G8B8A8_UNORM: case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB: case DXGI_FORMAT_R8G8B8A8_TYPELESS:
        return PixFmt::RGBA8;
    case DXGI_FORMAT_R10G10B10A2_UNORM: case DXGI_FORMAT_R10G10B10A2_TYPELESS:
        return PixFmt::RGB10A2;
    case DXGI_FORMAT_R16G16B16A16_FLOAT: case DXGI_FORMAT_R16G16B16A16_TYPELESS:
        return PixFmt::RGBA16F;
    default:
        return PixFmt::Unsupported;
    }
}

static void ReleaseTargets() {
    SafeRelease(g_rtv);
    SafeRelease(g_staging);
    SafeRelease(g_resolve);
    g_stagingDesc = {};
}

static void DoCapture(ID3D11Texture2D* bb, const D3D11_TEXTURE2D_DESC& d) {
    uint32_t x, y, w, h, seq;
    if (!PendingCapture(d.Width, d.Height, x, y, w, h, seq)) return;
    PixFmt pf = ToPixFmt(d.Format);
    if (pf == PixFmt::Unsupported) { CompleteCapture(seq, 0, 0, 1); return; }

    ID3D11Texture2D* src = bb;
    if (d.SampleDesc.Count > 1) {
        if (!g_resolve) {
            D3D11_TEXTURE2D_DESC rd = d;
            rd.SampleDesc.Count = 1; rd.SampleDesc.Quality = 0;
            rd.Usage = D3D11_USAGE_DEFAULT; rd.BindFlags = 0; rd.CPUAccessFlags = 0; rd.MiscFlags = 0;
            rd.MipLevels = 1; rd.ArraySize = 1;
            if (FAILED(g_dev->CreateTexture2D(&rd, nullptr, &g_resolve))) { CompleteCapture(seq, 0, 0, 2); return; }
        }
        g_ctx->ResolveSubresource(g_resolve, 0, bb, 0, TypedFormat(d.Format));
        src = g_resolve;
    }

    if (!g_staging || g_stagingDesc.Width != w || g_stagingDesc.Height != h || g_stagingDesc.Format != d.Format) {
        SafeRelease(g_staging);
        D3D11_TEXTURE2D_DESC sd = {};
        sd.Width = w; sd.Height = h; sd.MipLevels = 1; sd.ArraySize = 1; sd.Format = d.Format;
        sd.SampleDesc.Count = 1; sd.Usage = D3D11_USAGE_STAGING; sd.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        if (FAILED(g_dev->CreateTexture2D(&sd, nullptr, &g_staging))) { CompleteCapture(seq, 0, 0, 2); return; }
        g_stagingDesc = sd;
    }
    D3D11_BOX box = { x, y, 0, x + w, y + h, 1 };
    g_ctx->CopySubresourceRegion(g_staging, 0, 0, 0, 0, src, 0, &box);
    D3D11_MAPPED_SUBRESOURCE m = {};
    if (FAILED(g_ctx->Map(g_staging, 0, D3D11_MAP_READ, 0, &m))) { CompleteCapture(seq, 0, 0, 2); return; }
    for (uint32_t r = 0; r < h; r++)
        ConvertRow(pf, (const uint8_t*)m.pData + (size_t)r * m.RowPitch, g_shared->pixels + (size_t)r * w * 4, w);
    g_ctx->Unmap(g_staging, 0);
    CompleteCapture(seq, w, h, 0);
}

static void OnPresent(IDXGISwapChain* sc) {
    if (g_unsupported || !g_shared) return;
    if (!g_dev) {
        if (FAILED(sc->GetDevice(__uuidof(ID3D11Device), (void**)&g_dev))) {
            g_unsupported = true; // DX12 / DX10: pencere modu kullanılacak
            Logf("DXGI swapchain D3D11 değil, hook devre dışı");
            return;
        }
        if (!ClaimApi(Api::DX11)) { SafeRelease(g_dev); g_unsupported = true; return; }
        g_dev->GetImmediateContext(&g_ctx);
        Logf("D3D11 cihazı bulundu");
    }
    if (sc != g_sc) { ReleaseTargets(); g_sc = sc; }

    ID3D11Texture2D* bb = nullptr;
    if (FAILED(sc->GetBuffer(0, __uuidof(ID3D11Texture2D), (void**)&bb)) || !bb) return;
    D3D11_TEXTURE2D_DESC d;
    bb->GetDesc(&d);
    Heartbeat(Api::DX11, d.Width, d.Height);

    // Önce yakala (çizimimiz görüntüye girmesin), sonra çiz.
    DoCapture(bb, d);

    const SubtitleState& s = PollSubtitle();
    if (s.visible && !s.text.empty()) {
        if (!g_imguiReady) {
            ImGuiInitCommon();
            g_imguiReady = ImGui_ImplDX11_Init(g_dev, g_ctx);
        }
        if (!g_rtv) {
            D3D11_RENDER_TARGET_VIEW_DESC rd = {};
            rd.Format = TypedFormat(d.Format);
            rd.ViewDimension = d.SampleDesc.Count > 1 ? D3D11_RTV_DIMENSION_TEXTURE2DMS : D3D11_RTV_DIMENSION_TEXTURE2D;
            if (FAILED(g_dev->CreateRenderTargetView(bb, &rd, &g_rtv))) g_rtv = nullptr;
        }
        if (g_imguiReady && g_rtv) {
            ImGui::GetIO().DisplaySize = ImVec2((float)d.Width, (float)d.Height);
            ImGui::GetIO().DeltaTime = 1.f / 60.f;
            ImGui_ImplDX11_NewFrame();
            ImGui::NewFrame();
            DrawSubtitle((float)d.Width, (float)d.Height);
            ImGui::Render();

            ID3D11RenderTargetView* oldRtv[D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT] = {};
            ID3D11DepthStencilView* oldDsv = nullptr;
            g_ctx->OMGetRenderTargets(D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT, oldRtv, &oldDsv);
            g_ctx->OMSetRenderTargets(1, &g_rtv, nullptr);
            ImGui_ImplDX11_RenderDrawData(ImGui::GetDrawData());
            g_ctx->OMSetRenderTargets(D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT, oldRtv, oldDsv);
            for (auto*& r : oldRtv) SafeRelease(r);
            SafeRelease(oldDsv);
        }
    }
    bb->Release();
}

// Bazı sürücülerde Present içeride Present1'i çağırır; aynı karede iki kez çizmeyi önle.
static thread_local int t_depth = 0;

static HRESULT STDMETHODCALLTYPE hkPresent(IDXGISwapChain* sc, UINT sync, UINT flags) {
    if (t_depth++ == 0 && !(flags & DXGI_PRESENT_TEST)) OnPresent(sc);
    HRESULT hr = oPresent(sc, sync, flags);
    t_depth--;
    return hr;
}

static HRESULT STDMETHODCALLTYPE hkPresent1(IDXGISwapChain1* sc, UINT sync, UINT flags, const DXGI_PRESENT_PARAMETERS* p) {
    if (t_depth++ == 0 && !(flags & DXGI_PRESENT_TEST)) OnPresent(sc);
    HRESULT hr = oPresent1(sc, sync, flags, p);
    t_depth--;
    return hr;
}

static HRESULT STDMETHODCALLTYPE hkResizeBuffers(IDXGISwapChain* sc, UINT n, UINT w, UINT h, DXGI_FORMAT f, UINT fl) {
    ReleaseTargets();
    return oResizeBuffers(sc, n, w, h, f, fl);
}

static LRESULT CALLBACK DummyWndProc(HWND h, UINT m, WPARAM w, LPARAM l) { return DefWindowProcW(h, m, w, l); }

bool InstallDx11Hooks() {
    WNDCLASSEXW wc = { sizeof(wc), CS_CLASSDC, DummyWndProc, 0, 0, GetModuleHandleW(nullptr), nullptr, nullptr, nullptr, nullptr, L"GameSubTR_DX11Dummy", nullptr };
    RegisterClassExW(&wc);
    HWND hwnd = CreateWindowW(wc.lpszClassName, L"", WS_OVERLAPPEDWINDOW, 0, 0, 100, 100, nullptr, nullptr, wc.hInstance, nullptr);
    if (!hwnd) return false;

    DXGI_SWAP_CHAIN_DESC scd = {};
    scd.BufferCount = 1;
    scd.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    scd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    scd.OutputWindow = hwnd;
    scd.SampleDesc.Count = 1;
    scd.Windowed = TRUE;
    scd.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;

    // d3d11.dll'i statik import etmiyoruz: oyunun gerçekten DX11 kullanıp kullanmadığını
    // GetModuleHandle ile anlayabilmek için fonksiyon dinamik olarak alınır.
    auto create = reinterpret_cast<PFN_D3D11_CREATE_DEVICE_AND_SWAP_CHAIN>(
        GetProcAddress(GetModuleHandleW(L"d3d11.dll"), "D3D11CreateDeviceAndSwapChain"));
    if (!create) { DestroyWindow(hwnd); UnregisterClassW(wc.lpszClassName, wc.hInstance); return false; }
    IDXGISwapChain* sc = nullptr; ID3D11Device* dev = nullptr; ID3D11DeviceContext* ctx = nullptr;
    D3D_FEATURE_LEVEL fl;
    HRESULT hr = create(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION, &scd, &sc, &dev, &fl, &ctx);
    if (FAILED(hr))
        hr = create(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION, &scd, &sc, &dev, &fl, &ctx);
    bool ok = false;
    if (SUCCEEDED(hr)) {
        void** vt = *reinterpret_cast<void***>(sc);
        ok = MH_CreateHook(vt[8], (void*)&hkPresent, (void**)&oPresent) == MH_OK &&
             MH_CreateHook(vt[13], (void*)&hkResizeBuffers, (void**)&oResizeBuffers) == MH_OK;
        IDXGISwapChain1* sc1 = nullptr;
        if (ok && SUCCEEDED(sc->QueryInterface(__uuidof(IDXGISwapChain1), (void**)&sc1))) {
            void** vt1 = *reinterpret_cast<void***>(sc1);
            if (MH_CreateHook(vt1[22], (void*)&hkPresent1, (void**)&oPresent1) != MH_OK) oPresent1 = nullptr;
            sc1->Release();
        }
        if (ok) ok = MH_EnableHook(MH_ALL_HOOKS) == MH_OK;
        sc->Release(); dev->Release(); ctx->Release();
    }
    DestroyWindow(hwnd);
    UnregisterClassW(wc.lpszClassName, wc.hInstance);
    Logf("DX11 hook kurulumu: %s", ok ? "OK" : "BAŞARISIZ");
    return ok;
}

} // namespace gstr
