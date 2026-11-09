#include "common.h"
#include <cstdio>
#include <cstdarg>
#include <cmath>
#include <cstring>
#include <cfloat>
#include <algorithm>
#include "imgui.h"

namespace gstr {

Shared* g_shared = nullptr;
static volatile LONG g_api = 0;

bool ClaimApi(Api api) {
    LONG prev = InterlockedCompareExchange(&g_api, (LONG)api, 0);
    return prev == 0 || prev == (LONG)api;
}

void Logf(const char* fmt, ...) {
    char buf[1024];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);
    OutputDebugStringA("[GameSubTR] ");
    OutputDebugStringA(buf);
    OutputDebugStringA("\n");
}

static SubtitleState g_state;
static uint32_t g_lastSeq = 0xFFFFFFFF;

const SubtitleState& PollSubtitle() {
    if (!g_shared) return g_state;
    uint32_t seq = g_shared->textSeq;
    if (seq == g_lastSeq) return g_state;
    MemoryBarrier();
    SubtitleState s;
    uint32_t len = g_shared->textLen;
    if (len >= kTextCapacity) len = kTextCapacity - 1;
    s.text.assign(g_shared->text, len);
    s.visible = g_shared->visible != 0;
    s.fontSize = g_shared->fontSize > 4.f ? g_shared->fontSize : 30.f;
    s.textColor = g_shared->textColor;
    s.bgColor = g_shared->bgColor;
    s.box[0] = g_shared->boxX; s.box[1] = g_shared->boxY; s.box[2] = g_shared->boxW; s.box[3] = g_shared->boxH;
    s.flags = g_shared->layoutFlags;
    MemoryBarrier();
    if (g_shared->textSeq != seq) return g_state; // yazım sırasında okundu; bir sonraki karede tekrar dene
    g_state = std::move(s);
    g_lastSeq = seq;
    return g_state;
}

void Heartbeat(Api api, uint32_t bbW, uint32_t bbH) {
    if (!g_shared) return;
    g_shared->hookApi = (uint32_t)api;
    g_shared->backBufferW = bbW;
    g_shared->backBufferH = bbH;
    g_shared->heartbeat = GetTickCount();
}

bool PendingCapture(uint32_t bbW, uint32_t bbH, uint32_t& x, uint32_t& y, uint32_t& w, uint32_t& h, uint32_t& seq) {
    if (!g_shared || bbW == 0 || bbH == 0) return false;
    seq = g_shared->captureReqSeq;
    if (seq == g_shared->captureDoneSeq) return false;
    MemoryBarrier();
    auto clamp01 = [](float v) { return v < 0.f ? 0.f : (v > 1.f ? 1.f : v); };
    float fx = clamp01(g_shared->capX), fy = clamp01(g_shared->capY);
    float fw = clamp01(g_shared->capW), fh = clamp01(g_shared->capH);
    x = (uint32_t)std::lround(fx * bbW);
    y = (uint32_t)std::lround(fy * bbH);
    w = (uint32_t)std::max(1L, std::lround(fw * bbW));
    h = (uint32_t)std::max(1L, std::lround(fh * bbH));
    if (x >= bbW) x = bbW - 1;
    if (y >= bbH) y = bbH - 1;
    if (x + w > bbW) w = bbW - x;
    if (y + h > bbH) h = bbH - y;
    // Kapasiteyi aşarsa yüksekliği kırp.
    if ((uint64_t)w * h * 4 > kPixelCapacity) h = kPixelCapacity / (w * 4);
    return w > 0 && h > 0;
}

void CompleteCapture(uint32_t seq, uint32_t w, uint32_t h, uint32_t status) {
    if (!g_shared) return;
    g_shared->capWidth = w;
    g_shared->capHeight = h;
    g_shared->capStatus = status;
    MemoryBarrier();
    g_shared->captureDoneSeq = seq;
}

static inline float HalfToFloat(uint16_t h) {
    uint32_t sign = (h >> 15) & 1, exp = (h >> 10) & 0x1F, mant = h & 0x3FF;
    float v;
    if (exp == 0) v = std::ldexp((float)mant, -24);
    else if (exp == 31) v = mant ? 0.f : 65504.f;
    else v = std::ldexp((float)(mant | 0x400), (int)exp - 25);
    return sign ? -v : v;
}

static inline uint8_t LinearToSrgb8(float v) {
    if (v <= 0.f) return 0;
    if (v >= 1.f) return 255;
    float s = v <= 0.0031308f ? v * 12.92f : 1.055f * std::pow(v, 1.f / 2.4f) - 0.055f;
    return (uint8_t)std::lround(s * 255.f);
}

void ConvertRow(PixFmt fmt, const uint8_t* src, uint8_t* dst, uint32_t width) {
    switch (fmt) {
    case PixFmt::BGRA8:
        memcpy(dst, src, (size_t)width * 4);
        break;
    case PixFmt::RGBA8:
        for (uint32_t i = 0; i < width; i++) {
            dst[i * 4 + 0] = src[i * 4 + 2];
            dst[i * 4 + 1] = src[i * 4 + 1];
            dst[i * 4 + 2] = src[i * 4 + 0];
            dst[i * 4 + 3] = 255;
        }
        break;
    case PixFmt::RGB10A2: // DXGI R10G10B10A2: R düşük bitlerde
        for (uint32_t i = 0; i < width; i++) {
            uint32_t p; memcpy(&p, src + i * 4, 4);
            dst[i * 4 + 2] = (uint8_t)(((p >> 0) & 0x3FF) >> 2);
            dst[i * 4 + 1] = (uint8_t)(((p >> 10) & 0x3FF) >> 2);
            dst[i * 4 + 0] = (uint8_t)(((p >> 20) & 0x3FF) >> 2);
            dst[i * 4 + 3] = 255;
        }
        break;
    case PixFmt::RGBA16F: // scRGB doğrusal (HDR) → sRGB
        for (uint32_t i = 0; i < width; i++) {
            uint16_t c[4]; memcpy(c, src + i * 8, 8);
            dst[i * 4 + 2] = LinearToSrgb8(HalfToFloat(c[0]));
            dst[i * 4 + 1] = LinearToSrgb8(HalfToFloat(c[1]));
            dst[i * 4 + 0] = LinearToSrgb8(HalfToFloat(c[2]));
            dst[i * 4 + 3] = 255;
        }
        break;
    default:
        break;
    }
}

// --------------------------------------------------------------- ImGui

static ImFont* g_font = nullptr;
static constexpr float kFontBake = 64.f;

bool ImGuiInitCommon() {
    if (ImGui::GetCurrentContext()) return true;
    IMGUI_CHECKVERSION();
    ImGui::CreateContext();
    ImGuiIO& io = ImGui::GetIO();
    io.IniFilename = nullptr;
    io.LogFilename = nullptr;
    io.ConfigFlags |= ImGuiConfigFlags_NoMouseCursorChange;

    // Türkçe karakterler (ğ, ş, ı, İ ...) Latin Extended-A aralığında.
    static const ImWchar ranges[] = { 0x0020, 0x00FF, 0x0100, 0x017F, 0x2010, 0x205E, 0x20AC, 0x20AC, 0 };
    ImFontConfig cfg;
    cfg.OversampleH = 2;
    cfg.OversampleV = 2;
    char winDir[MAX_PATH] = {};
    GetWindowsDirectoryA(winDir, MAX_PATH);
    const char* candidates[] = { "\\Fonts\\segoeuib.ttf", "\\Fonts\\segoeui.ttf", "\\Fonts\\arialbd.ttf", "\\Fonts\\arial.ttf", "\\Fonts\\tahomabd.ttf", "\\Fonts\\tahoma.ttf", "\\Fonts\\verdana.ttf", "\\Fonts\\calibri.ttf" };
    for (const char* c : candidates) {
        std::string path = std::string(winDir) + c;
        if (GetFileAttributesA(path.c_str()) != INVALID_FILE_ATTRIBUTES) {
            g_font = io.Fonts->AddFontFromFileTTF(path.c_str(), kFontBake, &cfg, ranges);
            if (g_font) break;
        }
    }
    if (!g_font) g_font = io.Fonts->AddFontDefault();
    return true;
}

static ImU32 ArgbToImU32(uint32_t argb) {
    uint32_t a = (argb >> 24) & 0xFF, r = (argb >> 16) & 0xFF, g = (argb >> 8) & 0xFF, b = argb & 0xFF;
    return IM_COL32(r, g, b, a);
}

// Satırları kelime bazında sarar (ortalanmış çizim için).
static void WrapLines(ImFont* font, float size, const std::string& text, float maxWidth, std::vector<std::string>& out) {
    size_t start = 0;
    while (start <= text.size()) {
        size_t nl = text.find('\n', start);
        std::string para = text.substr(start, nl == std::string::npos ? std::string::npos : nl - start);
        std::string line;
        size_t i = 0;
        while (i < para.size()) {
            size_t sp = para.find(' ', i);
            std::string word = para.substr(i, sp == std::string::npos ? std::string::npos : sp - i);
            std::string candidate = line.empty() ? word : line + " " + word;
            float w = font->CalcTextSizeA(size, FLT_MAX, 0.f, candidate.c_str()).x;
            if (w > maxWidth && !line.empty()) {
                out.push_back(line);
                line = word;
            } else {
                line = candidate;
            }
            if (sp == std::string::npos) break;
            i = sp + 1;
        }
        out.push_back(line);
        if (nl == std::string::npos) break;
        start = nl + 1;
    }
}

void DrawSubtitle(float bbW, float bbH) {
    const SubtitleState& s = PollSubtitle();
    if (!s.visible || s.text.empty() || !g_font) return;
    ImDrawList* dl = ImGui::GetForegroundDrawList();
    float scale = bbH / 1080.f;
    float size = std::max(10.f, s.fontSize * scale);
    float padX = 14.f * scale, padY = 7.f * scale;

    float boxX = s.box[0] * bbW, boxY = s.box[1] * bbH, boxW = s.box[2] * bbW, boxH = s.box[3] * bbH;
    float maxW = std::min(std::max(boxW, bbW * 0.45f), bbW * 0.96f);

    std::vector<std::string> lines;
    WrapLines(g_font, size, s.text, maxW - 2 * padX, lines);
    float lineH = size * 1.18f;
    float contentW = 0.f;
    for (auto& l : lines) contentW = std::max(contentW, g_font->CalcTextSizeA(size, FLT_MAX, 0.f, l.c_str()).x);
    float w = contentW + 2 * padX, h = lines.size() * lineH + 2 * padY;
    bool cover = (s.flags & 2) != 0;
    if (cover) { w = std::max(w, std::min(boxW, maxW)); h = std::max(h, boxH); }

    float left = boxX + boxW / 2 - w / 2;
    float top = (s.flags & 1) ? boxY + boxH / 2 - h / 2 : boxY + boxH - h;
    left = std::clamp(left, 0.f, std::max(0.f, bbW - w));
    top = std::clamp(top, 0.f, std::max(0.f, bbH - h));

    dl->AddRectFilled(ImVec2(left, top), ImVec2(left + w, top + h), ArgbToImU32(s.bgColor), 6.f * scale);

    float y = top + (h - lines.size() * lineH) / 2 + (lineH - size) / 2;
    ImU32 col = ArgbToImU32(s.textColor | 0xFF000000);
    ImU32 outline = IM_COL32(0, 0, 0, 230);
    float o = std::max(1.f, 1.6f * scale);
    for (auto& l : lines) {
        float lw = g_font->CalcTextSizeA(size, FLT_MAX, 0.f, l.c_str()).x;
        ImVec2 p(left + (w - lw) / 2, y);
        const ImVec2 offs[] = { {-o, 0}, {o, 0}, {0, -o}, {0, o}, {-o, -o}, {o, o}, {-o, o}, {o, -o} };
        for (auto& d : offs) dl->AddText(g_font, size, ImVec2(p.x + d.x, p.y + d.y), outline, l.c_str());
        dl->AddText(g_font, size, p, col, l.c_str());
        y += lineH;
    }
}

} // namespace gstr
