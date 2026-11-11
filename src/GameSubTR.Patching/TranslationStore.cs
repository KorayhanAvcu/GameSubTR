using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using GameSubTR.Core.Logging;

namespace GameSubTR.Patching;

/// <summary>
/// Birebir (ordinal) anahtarlı kalıcı çeviri deposu (JSON Lines). Çeviri yarıda kesilirse
/// kaldığı yerden devam etmeyi sağlar. Core'daki TranslationCache'ten farkı: "Hello" ile "hello!" ayrı kayıttır.
/// </summary>
public sealed class TranslationStore
{
    private sealed record Entry(string s, string t);

    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly string _path;
    private readonly Dictionary<string, string> _map = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public TranslationStore(string filePath)
    {
        _path = filePath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
        if (!File.Exists(filePath)) return;
        foreach (var line in File.ReadLines(filePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var e = JsonSerializer.Deserialize<Entry>(line);
                if (e?.s != null && e.t != null) _map[e.s] = e.t;
            }
            catch (JsonException) { /* yarım yazılmış satırı atla */ }
        }
    }

    /// <summary>%LocalAppData%\GameSubTR\patcher\&lt;sha1(oyun yolu)&gt;\translations.jsonl</summary>
    public static string PathFor(string gameRoot)
    {
        var full = Path.GetFullPath(gameRoot).TrimEnd('\\', '/').ToLowerInvariant();
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(full))).ToLowerInvariant();
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GameSubTR", "patcher", hash, "translations.jsonl");
    }

    public int Count { get { lock (_gate) return _map.Count; } }

    public bool TryGet(string source, out string target)
    {
        lock (_gate) return _map.TryGetValue(source, out target!);
    }

    public void Put(string source, string target)
    {
        lock (_gate)
        {
            _map[source] = target;
            try { File.AppendAllText(_path, JsonSerializer.Serialize(new Entry(source, target), Json) + "\n"); }
            catch (IOException ex) { Log.Error("Çeviri deposu yazılamadı", ex); }
        }
    }
}
