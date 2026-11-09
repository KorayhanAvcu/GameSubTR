using System.Text.Json;
using GameSubTR.Core.Logging;

namespace GameSubTR.Core.Models;

/// <summary>Ayarları JSON olarak saklar (atomik yazma).</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _gate = new();
    public string FilePath { get; }

    public SettingsStore(string filePath) => FilePath = filePath;

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var s = JsonSerializer.Deserialize<AppSettings>(json, Options);
                if (s != null)
                {
                    // Sözlükler büyük/küçük harf duyarsız olmalı.
                    s.Profiles = new Dictionary<string, GameProfile>(s.Profiles, StringComparer.OrdinalIgnoreCase);
                    foreach (var p in s.Profiles.Values)
                        p.Glossary = new Dictionary<string, string>(p.Glossary, StringComparer.OrdinalIgnoreCase);
                    Migrate(s);
                    return s;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Ayar dosyası okunamadı, varsayılanlar kullanılıyor", ex);
            try { File.Copy(FilePath, FilePath + ".bad", true); } catch { /* yoksay */ }
        }
        return new AppSettings();
    }

    /// <summary>Eski ayar sürümlerini günceller.</summary>
    public static void Migrate(AppSettings s)
    {
        if (s.Version < 2)
        {
            // v1: çeviri orijinali siyah kutuyla kapatıyordu → v2: saydam, alanın üstünde.
            foreach (var p in s.Profiles.Values)
            {
                if (p.Placement == OverlayPlacement.OverOriginal) p.Placement = OverlayPlacement.AboveOriginal;
                p.BackgroundOpacity = 0.0;
            }
        }
        s.Version = AppSettings.CurrentVersion;
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
            File.Move(tmp, FilePath, overwrite: true);
        }
    }
}
