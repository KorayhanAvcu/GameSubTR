using System.Collections.Concurrent;
using System.Text.Json;
using GameSubTR.Core.Logging;
using GameSubTR.Core.Text;

namespace GameSubTR.Core.Translation;

/// <summary>
/// Oyun bazlı kalıcı çeviri önbelleği (JSON Lines). Aynı cümle tekrar geldiğinde
/// anında ve ücretsiz döner.
/// </summary>
public sealed class TranslationCache
{
    private readonly string _dir;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _games = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _writeGate = new();

    private sealed record Entry(string s, string t, string e);

    public TranslationCache(string directory)
    {
        _dir = directory;
        Directory.CreateDirectory(directory);
    }

    private string FileFor(string game)
    {
        var safe = string.Concat((string.IsNullOrWhiteSpace(game) ? "_global" : game).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(_dir, safe + ".jsonl");
    }

    private ConcurrentDictionary<string, string> Load(string game) => _games.GetOrAdd(game, g =>
    {
        var d = new ConcurrentDictionary<string, string>();
        var path = FileFor(g);
        if (!File.Exists(path)) return d;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var e = JsonSerializer.Deserialize<Entry>(line);
                    if (e != null) d[TextUtil.Key(e.s)] = e.t;
                }
                catch { /* bozuk satırı atla */ }
            }
        }
        catch (Exception ex) { Log.Error("Önbellek okunamadı", ex); }
        return d;
    });

    public bool TryGet(string game, string source, out string target)
    {
        var key = TextUtil.Key(source);
        target = "";
        return key.Length > 0 && Load(game).TryGetValue(key, out target!);
    }

    public void Put(string game, string source, string target, string engine)
    {
        var key = TextUtil.Key(source);
        if (key.Length == 0) return;
        Load(game)[key] = target;
        try
        {
            lock (_writeGate)
                File.AppendAllText(FileFor(game), JsonSerializer.Serialize(new Entry(source, target, engine)) + "\n");
        }
        catch (Exception ex) { Log.Error("Önbellek yazılamadı", ex); }
    }

    public void Clear(string game)
    {
        _games.TryRemove(game, out _);
        try { File.Delete(FileFor(game)); } catch { /* yoksay */ }
    }
}

public sealed record TranslationResult(string Source, string Text, string Engine, bool FromCache, TimeSpan Elapsed);

/// <summary>
/// Motor zinciri: sıradaki ilk yapılandırılmış motoru dener, hata/zaman aşımında
/// sonrakine geçer. Hata veren motor bir süre devre dışı bırakılır (circuit breaker).
/// Ayrıca oyun bazlı bağlam (son replikler) tutar.
/// </summary>
public sealed class TranslationService
{
    private readonly IReadOnlyList<ITranslator> _all;
    private readonly Func<IReadOnlyList<string>> _order;
    private readonly Func<int> _contextLines;
    private readonly TranslationCache? _cache;
    private readonly ConcurrentDictionary<string, DateTime> _disabledUntil = new();
    private readonly ConcurrentDictionary<string, LinkedList<string>> _context = new(StringComparer.OrdinalIgnoreCase);

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(7);
    public TimeSpan Cooldown { get; set; } = TimeSpan.FromSeconds(60);

    public TranslationService(IEnumerable<ITranslator> translators, Func<IReadOnlyList<string>> order, Func<int> contextLines, TranslationCache? cache)
    {
        _all = translators.ToList();
        _order = order;
        _contextLines = contextLines;
        _cache = cache;
    }

    public IReadOnlyList<ITranslator> ActiveChain()
    {
        var order = _order();
        var list = new List<ITranslator>();
        foreach (var id in order)
        {
            var t = _all.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (t != null && t.IsConfigured && !list.Contains(t)) list.Add(t);
        }
        // Sıralamada olmayan ama yapılandırılmış motorlar sona.
        foreach (var t in _all) if (t.IsConfigured && !list.Contains(t)) list.Add(t);
        return list;
    }

    public async Task<TranslationResult> TranslateAsync(string text, string game, IReadOnlyDictionary<string, string> glossary, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (_cache != null && _cache.TryGet(game, text, out var cached))
        {
            AddContext(game, text);
            return new(text, cached, "cache", true, sw.Elapsed);
        }

        var ctx = GetContext(game);
        var request = new TranslationRequest(text, ctx, glossary, game);
        var errors = new List<string>();
        var chain = ActiveChain();
        if (chain.Count == 0) throw new TranslationException("Yapılandırılmış çeviri motoru yok. Ayarlardan bir motor ekleyin.");

        foreach (var t in chain)
        {
            if (_disabledUntil.TryGetValue(t.Id, out var until) && until > DateTime.UtcNow && t != chain[^1])
                continue;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);
            try
            {
                var result = await t.TranslateAsync(request, cts.Token).ConfigureAwait(false);
                _disabledUntil.TryRemove(t.Id, out _);
                _cache?.Put(game, text, result, t.Id);
                AddContext(game, text);
                return new(text, result, t.Id, false, sw.Elapsed);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                var msg = ex is OperationCanceledException ? "zaman aşımı" : ex.Message;
                errors.Add($"{t.DisplayName}: {msg}");
                Log.Warn($"Çeviri motoru başarısız ({t.Id}): {msg}");
                _disabledUntil[t.Id] = DateTime.UtcNow + Cooldown;
            }
        }
        throw new TranslationException("Tüm çeviri motorları başarısız: " + string.Join(" | ", errors));
    }

    private IReadOnlyList<string> GetContext(string game)
    {
        if (!_context.TryGetValue(game, out var list)) return Array.Empty<string>();
        lock (list) return list.ToList();
    }

    private void AddContext(string game, string text)
    {
        var list = _context.GetOrAdd(game, _ => new LinkedList<string>());
        lock (list)
        {
            list.AddLast(text);
            while (list.Count > Math.Max(0, _contextLines())) list.RemoveFirst();
        }
    }
}

public static class GlossaryParser
{
    /// <summary>"Kaynak = Hedef" satırları; hedef boşsa terim çevrilmeden korunur.</summary>
    public static Dictionary<string, string> Parse(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var idx = line.IndexOf('=');
            if (idx < 0) d[line] = line;
            else
            {
                var k = line[..idx].Trim(); var v = line[(idx + 1)..].Trim();
                if (k.Length > 0) d[k] = v.Length == 0 ? k : v;
            }
        }
        return d;
    }

    public static string Format(IReadOnlyDictionary<string, string> glossary) =>
        string.Join("\n", glossary.Select(kv => kv.Key == kv.Value ? kv.Key : $"{kv.Key} = {kv.Value}"));
}
