using System.Text.Json.Serialization;

namespace GameSubTR.Core.Models;

/// <summary>Altyazının kaynağı.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceMode { Ocr, Audio, Both }

/// <summary>Çevirinin ekranda nasıl gösterileceği.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PresenterMode { Auto, Window, Hook }

/// <summary>Çeviri altyazısının konumu.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OverlayPlacement
{
    /// <summary>Orijinal altyazının tam üstüne (onu kapatır).</summary>
    OverOriginal,
    /// <summary>Ekranda sabit bir dikey konumda.</summary>
    Fixed
}

/// <summary>OCR öncesi metin rengi ayrıştırma.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OcrColorFilter { Auto, White, Yellow, None }

/// <summary>0..1 aralığında, oyunun bulunduğu monitöre göre normalize dikdörtgen.</summary>
public sealed record NormalizedRect(double X, double Y, double Width, double Height)
{
    public bool IsValid => Width > 0.005 && Height > 0.005 && X >= 0 && Y >= 0 && X + Width <= 1.0001 && Y + Height <= 1.0001;

    public (int X, int Y, int W, int H) ToPixels(int screenWidth, int screenHeight)
    {
        int x = (int)Math.Round(X * screenWidth);
        int y = (int)Math.Round(Y * screenHeight);
        int w = Math.Max(1, (int)Math.Round(Width * screenWidth));
        int h = Math.Max(1, (int)Math.Round(Height * screenHeight));
        x = Math.Clamp(x, 0, Math.Max(0, screenWidth - 1));
        y = Math.Clamp(y, 0, Math.Max(0, screenHeight - 1));
        w = Math.Min(w, screenWidth - x);
        h = Math.Min(h, screenHeight - y);
        return (x, y, w, h);
    }

    public static NormalizedRect FromPixels(int x, int y, int w, int h, int screenWidth, int screenHeight) =>
        new((double)x / screenWidth, (double)y / screenHeight, (double)w / screenWidth, (double)h / screenHeight);
}

/// <summary>Oyuna özel profil. Anahtar: exe adı (küçük harf).</summary>
public sealed class GameProfile
{
    public string ExeName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public SourceMode Mode { get; set; } = SourceMode.Ocr;
    public PresenterMode Presenter { get; set; } = PresenterMode.Auto;
    public NormalizedRect? Region { get; set; }

    public OcrColorFilter ColorFilter { get; set; } = OcrColorFilter.Auto;
    public int OcrIntervalMs { get; set; } = 250;

    public OverlayPlacement Placement { get; set; } = OverlayPlacement.OverOriginal;
    /// <summary>Fixed modda altyazı bloğunun alt kenarının ekran yüksekliğine oranı.</summary>
    public double FixedBottom { get; set; } = 0.92;
    public double FontSize { get; set; } = 30;
    public string TextColor { get; set; } = "#FFFFFF";
    public double BackgroundOpacity { get; set; } = 0.70;
    public bool ShowOriginal { get; set; }

    /// <summary>Çevrilmeyecek / sabit çevrilecek terimler (karakter adları vb.). Kaynak → hedef.</summary>
    public Dictionary<string, string> Glossary { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static GameProfile CreateDefault(string exeName) => new()
    {
        ExeName = exeName.ToLowerInvariant(),
        DisplayName = Path.GetFileNameWithoutExtension(exeName)
    };
}

public sealed class HotkeySettings
{
    public string Toggle { get; set; } = "Ctrl+Shift+T";
    public string SelectRegion { get; set; } = "Ctrl+Shift+R";
    public string CycleMode { get; set; } = "Ctrl+Shift+M";
    public string ToggleOriginal { get; set; } = "Ctrl+Shift+O";
}

public sealed class TranslationSettings
{
    public string? AnthropicApiKey { get; set; }
    public string AnthropicModel { get; set; } = "claude-haiku-5-5";

    /// <summary>OpenAI uyumlu uç nokta (OpenAI, OpenRouter, LM Studio, Ollama...). Ollama için offline çalışır.</summary>
    public string? OpenAiBaseUrl { get; set; }
    public string? OpenAiApiKey { get; set; }
    public string? OpenAiModel { get; set; }

    public string? DeepLApiKey { get; set; }

    /// <summary>Anahtarsız ücretsiz Google uç noktası; son çare yedek.</summary>
    public bool UseGoogleFree { get; set; } = true;

    /// <summary>LLM'e gönderilecek önceki replik sayısı (bağlam).</summary>
    public int ContextLines { get; set; } = 6;

    /// <summary>Motor sırası. Bilinmeyen/yapılandırılmamış motorlar atlanır.</summary>
    public List<string> Order { get; set; } = new() { "claude", "openai", "deepl", "google" };
}

public sealed class AudioSettings
{
    /// <summary>ggml model dosya adı (huggingface ggerganov/whisper.cpp).</summary>
    public string WhisperModel { get; set; } = "ggml-large-v3-turbo-q5_0.bin";
    public float VadThreshold { get; set; } = 0.5f;
    public int MinSilenceMs { get; set; } = 550;
    public int MaxSegmentMs { get; set; } = 12000;
    /// <summary>true: sadece oyunun sesi (process loopback). false: tüm sistem sesi.</summary>
    public bool CaptureGameOnly { get; set; } = true;
}

public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public HotkeySettings Hotkeys { get; set; } = new();
    public TranslationSettings Translation { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
    public Dictionary<string, GameProfile> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Profili olmayan ve oyun olmayan programlar (izlenmez).</summary>
    public List<string> IgnoredExes { get; set; } = new()
    {
        "explorer.exe", "gamesubtr.exe", "devenv.exe", "code.exe", "chrome.exe", "msedge.exe", "firefox.exe",
        "discord.exe", "steam.exe", "steamwebhelper.exe", "epicgameslauncher.exe", "searchhost.exe",
        "shellexperiencehost.exe", "startmenuexperiencehost.exe", "taskmgr.exe", "applicationframehost.exe",
        "windowsterminal.exe", "cmd.exe", "powershell.exe", "notepad.exe", "lockapp.exe", "textinputhost.exe"
    };

    public GameProfile GetOrCreateProfile(string exeName)
    {
        var key = exeName.ToLowerInvariant();
        if (!Profiles.TryGetValue(key, out var p))
        {
            p = GameProfile.CreateDefault(key);
            Profiles[key] = p;
        }
        return p;
    }
}
