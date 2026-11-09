#pragma once
#include <windows.h>
#include <string>
#include <vector>
#include "shared.h"

namespace gstr {

extern Shared* g_shared;

// Hangi API ImGui'yi sahiplendi (aynı süreçte DX9 ve DX11 çakışmasın).
enum class Api { None = 0, DX9 = 9, DX11 = 11 };
bool ClaimApi(Api api);

void Logf(const char* fmt, ...);

// Paylaşımlı bellekten altyazı durumunu okur (render iş parçacığında).
struct SubtitleState {
    std::string text;
    bool visible = false;
    float fontSize = 30.f;
    uint32_t textColor = 0xFFFFFFFF;
    uint32_t bgColor = 0xB4000000;
    float box[4] = {0.1f, 0.8f, 0.8f, 0.12f};
    uint32_t flags = 0;
};
const SubtitleState& PollSubtitle();

void Heartbeat(Api api, uint32_t bbW, uint32_t bbH);

// Yakalama isteği var mı? Varsa piksel dikdörtgenini döndürür.
bool PendingCapture(uint32_t bbW, uint32_t bbH, uint32_t& x, uint32_t& y, uint32_t& w, uint32_t& h, uint32_t& seq);
void CompleteCapture(uint32_t seq, uint32_t w, uint32_t h, uint32_t status);

// Piksel dönüşümleri (hedef: BGRA8).
enum class PixFmt { Unsupported, BGRA8, RGBA8, RGB10A2, RGBA16F };
void ConvertRow(PixFmt fmt, const uint8_t* src, uint8_t* dst, uint32_t width);

// ImGui ortak kurulum ve çizim.
bool ImGuiInitCommon();
void DrawSubtitle(float bbW, float bbH);

} // namespace gstr
