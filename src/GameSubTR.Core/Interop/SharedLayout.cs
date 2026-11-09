namespace GameSubTR.Core.Interop;

/// <summary>
/// Uygulama ile hook DLL arasındaki paylaşımlı bellek düzeni.
/// native/hook/shared.h ile BİREBİR aynı olmalıdır.
/// İsim: "Local\GameSubTR_{pid}".
/// </summary>
public static class SharedLayout
{
    public const uint Magic = 0x52545347; // "GSTR"
    public const uint Version = 1;

    public const int OffMagic = 0;
    public const int OffVersion = 4;
    public const int OffTextSeq = 8;
    public const int OffTextLen = 12;
    public const int OffVisible = 16;
    public const int OffFontSize = 20;      // float, 1080p'ye göre piksel
    public const int OffTextColor = 24;     // 0xAARRGGBB
    public const int OffBgColor = 28;       // 0xAARRGGBB
    public const int OffBoxX = 32;          // float, normalize hedef kutu
    public const int OffBoxY = 36;
    public const int OffBoxW = 40;
    public const int OffBoxH = 44;
    public const int OffLayoutFlags = 48;   // 1 = kutuda dikey ortala, 0 = kutunun altına hizala
    public const int OffCaptureReqSeq = 56;
    public const int OffCapX = 60;          // float normalize
    public const int OffCapY = 64;
    public const int OffCapW = 68;
    public const int OffCapH = 72;
    public const int OffCaptureDoneSeq = 76;
    public const int OffCapWidth = 80;
    public const int OffCapHeight = 84;
    public const int OffCapStatus = 88;     // 0 = BGRA hazır, 1 = desteklenmeyen format, 2 = hata
    public const int OffHeartbeat = 92;     // DLL her karede GetTickCount yazar
    public const int OffHookApi = 96;       // 0 = yok, 9 = DX9, 11 = DX11
    public const int OffBackBufferW = 100;
    public const int OffBackBufferH = 104;
    public const int OffText = 128;
    public const int TextCapacity = 8192;
    public const int OffPixels = OffText + TextCapacity;          // 8320
    public const int PixelCapacity = 3840 * 2160 * 4;
    public const int TotalSize = OffPixels + PixelCapacity;

    public static string MappingName(int pid) => $"Local\\GameSubTR_{pid}";
}
