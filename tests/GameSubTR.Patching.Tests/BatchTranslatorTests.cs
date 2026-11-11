using System.Text.RegularExpressions;
using GameSubTR.Core.Translation;
using Xunit;

namespace GameSubTR.Patching.Tests;

internal sealed class FakeTranslator(Func<TranslationRequest, string> f) : ITranslator
{
    public int Calls;
    public List<string> Inputs = new();
    public string Id => "fake";
    public string DisplayName => "fake";
    public bool IsConfigured => true;
    public Task<string> TranslateAsync(TranslationRequest r, CancellationToken ct)
    {
        Calls++; Inputs.Add(r.Text);
        return Task.FromResult(f(r));
    }

    /// <summary>Her satırda "[n] " ön ekini koruyup metnin başına TR: ekler.</summary>
    public static string Good(TranslationRequest r) =>
        string.Join("\n", r.Text.Split('\n').Select(l =>
        {
            var m = Regex.Match(l, @"^(\[\d+\] )(.*)$");
            return m.Success ? m.Groups[1].Value + "TR:" + m.Groups[2].Value : "TR:" + l;
        }));

    public static TranslationService Service(FakeTranslator t) =>
        new(new ITranslator[] { t }, () => new[] { "fake" }, () => 0, null);
}

public class BatchTranslatorTests
{
    private static readonly Dictionary<string, string> NoGlossary = new();

    private static string StorePath() => Path.Combine(TestGames.NewDir("store"), "translations.jsonl");

    private static List<TextUnit> Units(params string[] sources) =>
        sources.Select((s, i) => new TextUnit("u" + i, s, null)).ToList();

    private static Task<BatchResult> Run(FakeTranslator t, TranslationStore store, List<TextUnit> units, int batchSize = 40, CancellationToken ct = default) =>
        new BatchTranslator(FakeTranslator.Service(t), store, batchSize).TranslateAsync(units, EngineKind.RenPy, "game", NoGlossary, null, ct);

    [Fact]
    public async Task Batch_TranslatesAndRestoresPlaceholders()
    {
        var t = new FakeTranslator(FakeTranslator.Good);
        var r = await Run(t, new TranslationStore(StorePath()), Units("Hi [player]", "Line one\nLine two"));
        Assert.Equal("TR:Hi [player]", r.Translations["Hi [player]"]);
        Assert.Equal("TR:Line one\nLine two", r.Translations["Line one\nLine two"]);
        Assert.Empty(r.Failed);
    }

    [Fact]
    public async Task Batch_SendsOneRequestPerBatch()
    {
        var t = new FakeTranslator(FakeTranslator.Good);
        var r = await Run(t, new TranslationStore(StorePath()), Units("One apple", "Two apples", "Three apples", "Four apples", "Five apples"), batchSize: 5);
        Assert.Equal(1, t.Calls);
        Assert.Equal(5, r.Translations.Count);
        Assert.StartsWith("[1] One apple\n[2] Two apples", t.Inputs[0]);
    }

    [Fact]
    public async Task Batch_SplitsByBatchSize()
    {
        var t = new FakeTranslator(FakeTranslator.Good);
        await Run(t, new TranslationStore(StorePath()), Units("One apple", "Two apples", "Three apples", "Four apples", "Five apples"), batchSize: 2);
        Assert.Equal(3, t.Calls);
    }

    [Fact]
    public async Task Batch_FallsBackWhenMarkersLost()
    {
        // Çok satırlı girdide işaretleri siler; tek birimlik girdiyi düzgün çevirir.
        var t = new FakeTranslator(r => r.Text.Contains('\n') ? "tamamen bozuk yanıt" : "TR:" + r.Text);
        var r = await Run(t, new TranslationStore(StorePath()), Units("One apple", "Two apples", "Three apples"));
        Assert.Equal(1 + 3, t.Calls);
        Assert.Equal(3, r.Translations.Count);
        Assert.Equal("TR:Two apples", r.Translations["Two apples"]);
        Assert.Empty(r.Failed);
    }

    [Fact]
    public async Task Batch_FallsBackWhenLineMissing()
    {
        var t = new FakeTranslator(r => r.Text.Contains('\n') ? "[1] TR:bir" : "TR:" + r.Text);
        var r = await Run(t, new TranslationStore(StorePath()), Units("One apple", "Two apples"));
        Assert.Equal(3, t.Calls);
        Assert.Equal("TR:One apple", r.Translations["One apple"]);
    }

    [Fact]
    public async Task Unit_FailsWhenPlaceholderDropped()
    {
        var t = new FakeTranslator(r => Regex.Replace(FakeTranslator.Good(r), @"<x\d+/>", ""));
        var r = await Run(t, new TranslationStore(StorePath()), Units("Hi [player]", "Plain text"));
        Assert.Equal(new[] { "Hi [player]" }, r.Failed);
        Assert.False(r.Translations.ContainsKey("Hi [player]"));
        Assert.Equal("TR:Plain text", r.Translations["Plain text"]);
    }

    [Fact]
    public async Task Unit_FailsWhenEngineThrowsOrReturnsEmpty()
    {
        var t = new FakeTranslator(r => r.Text.Contains("boom") ? throw new TranslationException("down") : " ");
        var r = await Run(t, new TranslationStore(StorePath()), Units("boom boom", "Empty answer"));
        Assert.Equal(2, r.Failed.Count);
        Assert.Empty(r.Translations);
    }

    [Fact]
    public async Task Skips_NonTranslatable_And_Dedupes()
    {
        var t = new FakeTranslator(FakeTranslator.Good);
        var r = await Run(t, new TranslationStore(StorePath()), Units("Hello", "Hello", "123"));
        Assert.Equal(1, t.Calls);
        Assert.Equal("Hello", t.Inputs[0]);
        Assert.Equal(1, r.Skipped);
        Assert.Single(r.Translations);
    }

    [Fact]
    public async Task Resume_UsesStore()
    {
        var path = StorePath();
        var t = new FakeTranslator(FakeTranslator.Good);
        await Run(t, new TranslationStore(path), Units("One apple", "Two apples"));
        var calls = t.Calls;
        var r = await Run(t, new TranslationStore(path), Units("One apple", "Two apples"));
        Assert.Equal(calls, t.Calls);
        Assert.Equal("TR:One apple", r.Translations["One apple"]);
    }

    [Fact]
    public void Store_IsExactMatch()
    {
        var store = new TranslationStore(StorePath());
        store.Put("Hello", "Merhaba");
        Assert.True(store.TryGet("Hello", out var v));
        Assert.Equal("Merhaba", v);
        Assert.False(store.TryGet("hello!", out _));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public async Task Cancel_KeepsFinishedWork()
    {
        var path = StorePath();
        using var cts = new CancellationTokenSource();
        var t = new FakeTranslator(r =>
        {
            var o = FakeTranslator.Good(r);
            cts.Cancel(); // ilk gruptan sonra iptal
            return o;
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Run(t, new TranslationStore(path), Units("One apple", "Two apples", "Three apples", "Four apples"), batchSize: 2, ct: cts.Token));
        var store = new TranslationStore(path);
        Assert.Equal(2, store.Count);
        Assert.True(store.TryGet("One apple", out _));
    }

    [Fact]
    public async Task Progress_ReportsTotals()
    {
        var reports = new List<BatchProgress>();
        var t = new FakeTranslator(FakeTranslator.Good);
        var bt = new BatchTranslator(FakeTranslator.Service(t), new TranslationStore(StorePath()), 2);
        await bt.TranslateAsync(Units("One apple", "Two apples", "Three apples"), EngineKind.RenPy, "g", NoGlossary, new SyncProgress(reports.Add), default);
        Assert.Equal(3, reports[^1].Total);
        Assert.Equal(3, reports[^1].Done);
    }

    private sealed class SyncProgress(Action<BatchProgress> a) : IProgress<BatchProgress>
    {
        public void Report(BatchProgress value) => a(value);
    }
}
