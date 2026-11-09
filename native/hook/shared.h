// Uygulama ile hook DLL arasındaki paylaşımlı bellek düzeni.
// src/GameSubTR.Core/Interop/SharedLayout.cs ile BİREBİR aynı olmalıdır.
#pragma once
#include <cstdint>
#include <cstddef>

namespace gstr {

constexpr uint32_t kMagic = 0x52545347; // "GSTR"
constexpr uint32_t kVersion = 1;
constexpr uint32_t kTextCapacity = 8192;
constexpr uint32_t kPixelCapacity = 3840u * 2160u * 4u;

#pragma pack(push, 1)
struct Shared {
    uint32_t magic;            // 0
    uint32_t version;          // 4
    volatile uint32_t textSeq; // 8
    uint32_t textLen;          // 12
    uint32_t visible;          // 16
    float fontSize;            // 20 (1080p'ye göre piksel)
    uint32_t textColor;        // 24 0xAARRGGBB
    uint32_t bgColor;          // 28 0xAARRGGBB
    float boxX, boxY, boxW, boxH; // 32..44 normalize
    uint32_t layoutFlags;      // 48 bit0: kutuda dikey ortala, bit1: kutuyu kapla
    uint32_t reserved0;        // 52
    volatile uint32_t captureReqSeq; // 56
    float capX, capY, capW, capH;    // 60..72 normalize
    volatile uint32_t captureDoneSeq; // 76
    uint32_t capWidth;         // 80
    uint32_t capHeight;        // 84
    uint32_t capStatus;        // 88 0=BGRA hazır, 1=desteklenmeyen format, 2=hata
    volatile uint32_t heartbeat; // 92 GetTickCount
    uint32_t hookApi;          // 96 0/9/11
    uint32_t backBufferW;      // 100
    uint32_t backBufferH;      // 104
    uint8_t reserved1[128 - 108];
    char text[kTextCapacity];  // 128
    uint8_t pixels[kPixelCapacity]; // 8320
};
#pragma pack(pop)

static_assert(offsetof(Shared, textSeq) == 8, "layout");
static_assert(offsetof(Shared, fontSize) == 20, "layout");
static_assert(offsetof(Shared, layoutFlags) == 48, "layout");
static_assert(offsetof(Shared, captureReqSeq) == 56, "layout");
static_assert(offsetof(Shared, captureDoneSeq) == 76, "layout");
static_assert(offsetof(Shared, heartbeat) == 92, "layout");
static_assert(offsetof(Shared, backBufferH) == 104, "layout");
static_assert(offsetof(Shared, text) == 128, "layout");
static_assert(offsetof(Shared, pixels) == 8320, "layout");

constexpr size_t kTotalSize = sizeof(Shared);

} // namespace gstr
