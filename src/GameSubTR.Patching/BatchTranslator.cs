using System.Text;
using System.Text.RegularExpressions;
using GameSubTR.Core.Logging;
using GameSubTR.Core.Translation;

namespace GameSubTR.Patching;

public sealed record BatchProgress(int Done, int Total, int Failed);

/// <param name="Translations">Kaynak → Türkçe (yalnızca başarıyla çevrilenler).</param>
/// <param name="Failed">Çevrilemeyen kaynak metinler (oyunda orijinal kalır).</param>
/// <param name="Skipped">Çevrilecek metin içermediği için atlanan tekil birim sayısı.</param>
public sealed record BatchResult(IReadOnlyDictionary<string, string> Translations, IReadOnlyList<string> Failed, int Skipped);

/// <summary>
/// Metin birimlerini gruplar halinde çevirir: her grup "[n] metin" satırları olarak tek istekte gider.
/// Yanıtta numaralar veya yer tutucular bozulursa grup birim birim yeniden çevrilir.
/// Her başarılı çeviri anında depoya yazılır; iptal edilen iş kaldığı yerden devam eder.
/// </summary>
public sealed partial class BatchTranslator
{
    private readonly TranslationService _service;
    private readonly TranslationStore _store;
    private readonly int _batchSize;
    private readonly int _maxBatchChars;

    [GeneratedRegex(@"^\s*\[(\d+)\]\s?(.*)$")]
    private static partial Regex LineRegex();

    public BatchTranslator(TranslationService service, TranslationStore store, int batchSize = 40, int maxBatchChars = 1800)
    {
        _service = service;
        _store = store;
        _batchSize = Math.Max(1, batchSize);
        _maxBatchChars = Math.Max(1, maxBatchChars);
    }

    public async Task<BatchResult> TranslateAsync(IReadOnlyList<TextUnit> units, EngineKind engine, string gameName,
        IReadOnlyDictionary<string, string> glossary, IProgress<BatchProgress>? progress, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var failed = new List<string>();
        var pending = new List<(string Source, MaskedText Masked)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var skipped = 0;

        foreach (var u in units)
        {
            if (!seen.Add(u.Source)) continue;
            if (!PlaceholderGuard.IsTranslatable(u.Source, engine)) { skipped++; continue; }
            if (_store.TryGet(u.Source, out var cached)) { result[u.Source] = cached; continue; }
            pending.Add((u.Source, PlaceholderGuard.Mask(u.Source, engine)));
        }

        var total = result.Count + pending.Count;
        progress?.Report(new BatchProgress(result.Count, total, 0));

        foreach (var batch in Split(pending))
        {
            ct.ThrowIfCancellationRequested();
            var done = batch.Count > 1 ? await TryBatchAsync(batch, gameName, glossary, ct).ConfigureAwait(false) : null;
            if (done == null)
            {
                done = new string?[batch.Count];
                for (var i = 0; i < batch.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    done[i] = await TryUnitAsync(batch[i].Masked, gameName, glossary, ct).ConfigureAwait(false);
                    if (done[i] != null) Save(batch[i].Source, done[i]!, result);
                }
            }
            else
            {
                for (var i = 0; i < batch.Count; i++) Save(batch[i].Source, done[i]!, result);
            }
            for (var i = 0; i < batch.Count; i++)
                if (done[i] == null) failed.Add(batch[i].Source);
            progress?.Report(new BatchProgress(result.Count + failed.Count, total, failed.Count));
        }

        return new BatchResult(result, failed, skipped);
    }

    private void Save(string source, string target, Dictionary<string, string> result)
    {
        result[source] = target;
        _store.Put(source, target);
    }

    private IEnumerable<List<(string Source, MaskedText Masked)>> Split(List<(string Source, MaskedText Masked)> pending)
    {
        var batch = new List<(string, MaskedText)>();
        var chars = 0;
        foreach (var p in pending)
        {
            var len = p.Masked.Text.Length + 6;
            if (batch.Count > 0 && (batch.Count >= _batchSize || chars + len > _maxBatchChars))
            {
                yield return batch;
                batch = new List<(string, MaskedText)>();
                chars = 0;
            }
            batch.Add(p);
            chars += len;
        }
        if (batch.Count > 0) yield return batch;
    }

    /// <summary>Grubu tek istekte çevirir; herhangi bir satır doğrulanamazsa null (birim birim denenecek).</summary>
    private async Task<string?[]?> TryBatchAsync(List<(string Source, MaskedText Masked)> batch, string game,
        IReadOnlyDictionary<string, string> glossary, CancellationToken ct)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < batch.Count; i++)
        {
            if (i > 0) sb.Append('\n');
            sb.Append('[').Append(i + 1).Append("] ").Append(batch[i].Masked.Text);
        }

        string response;
        try { response = (await _service.TranslateAsync(sb.ToString(), game, glossary, ct).ConfigureAwait(false)).Text; }
        catch (TranslationException ex)
        {
            Log.Warn("Toplu çeviri başarısız, birim birim denenecek: " + ex.Message);
            return null;
        }

        var outputs = new string?[batch.Count];
        foreach (var line in response.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var m = LineRegex().Match(line.TrimEnd('\r'));
            if (!m.Success) return null;
            if (!int.TryParse(m.Groups[1].Value, out var n) || n < 1 || n > batch.Count || outputs[n - 1] != null) return null;
            if (!PlaceholderGuard.TryRestore(m.Groups[2].Value.Trim(), batch[n - 1].Masked, out var restored) || restored.Length == 0) return null;
            outputs[n - 1] = restored;
        }
        return outputs.Any(o => o == null) ? null : outputs;
    }

    private async Task<string?> TryUnitAsync(MaskedText masked, string game, IReadOnlyDictionary<string, string> glossary, CancellationToken ct)
    {
        try
        {
            var text = (await _service.TranslateAsync(masked.Text, game, glossary, ct).ConfigureAwait(false)).Text;
            return PlaceholderGuard.TryRestore(text.Trim(), masked, out var restored) && restored.Length > 0 ? restored : null;
        }
        catch (TranslationException ex)
        {
            Log.Warn("Birim çevrilemedi: " + ex.Message);
            return null;
        }
    }
}
