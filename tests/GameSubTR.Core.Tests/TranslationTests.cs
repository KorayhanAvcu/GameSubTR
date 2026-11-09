using GameSubTR.Core.Translation;
using Xunit;

namespace GameSubTR.Core.Tests;

public class TranslationTests
{
    private sealed class FakeTranslator(string id, Func<TranslationRequest, string> f, bool configured = true) : ITranslator
    {
        public int Calls;
        public TranslationRequest? Last;
        public string Id => id;
        public string DisplayName => id;
        public bool IsConfigured => configured;
        public Task<string> TranslateAsync(TranslationRequest r, CancellationToken ct)
        {
            Calls++; Last = r;
            return Task.FromResult(f(r));
        }
    }

    private static readonly Dictionary<string, string> NoGlossary = new();

    private static string TempDir() => Path.Combine(Path.GetTempPath(), "gstr-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Chain_FallsBackOnFailure_AndUsesCache()
    {
        var bad = new FakeTranslator("claude", _ => throw new TranslationException("down"));
        var good = new FakeTranslator("google", r => "TR:" + r.Text);
        var svc = new TranslationService(new ITranslator[] { bad, good }, () => new[] { "claude", "google" }, () => 4, new TranslationCache(TempDir()));

        var r1 = await svc.TranslateAsync("Hello", "game.exe", NoGlossary, default);
        Assert.Equal("TR:Hello", r1.Text);
        Assert.Equal("google", r1.Engine);

        var r2 = await svc.TranslateAsync("hello!", "game.exe", NoGlossary, default);
        Assert.True(r2.FromCache);
        Assert.Equal(1, good.Calls);
        // Hatalı motor bekleme süresinde atlanır.
        await svc.TranslateAsync("Another", "game.exe", NoGlossary, default);
        Assert.Equal(1, bad.Calls);
    }

    [Fact]
    public async Task Chain_SkipsUnconfigured_AndPassesContext()
    {
        var off = new FakeTranslator("claude", _ => "x", configured: false);
        var llm = new FakeTranslator("openai", r => "ok");
        var svc = new TranslationService(new ITranslator[] { off, llm }, () => new[] { "claude", "openai" }, () => 2, null);
        await svc.TranslateAsync("one", "g", NoGlossary, default);
        await svc.TranslateAsync("two", "g", NoGlossary, default);
        await svc.TranslateAsync("three", "g", NoGlossary, default);
        Assert.Equal(0, off.Calls);
        Assert.Equal(new[] { "one", "two" }, llm.Last!.Context);
    }

    [Fact]
    public async Task Chain_ThrowsWhenNothingConfigured()
    {
        var svc = new TranslationService(Array.Empty<ITranslator>(), () => Array.Empty<string>(), () => 0, null);
        await Assert.ThrowsAsync<TranslationException>(() => svc.TranslateAsync("x", "g", NoGlossary, default));
    }

    [Fact]
    public void Cache_PersistsAcrossInstances()
    {
        var dir = TempDir();
        new TranslationCache(dir).Put("game.exe", "Good morning", "Günaydın", "test");
        Assert.True(new TranslationCache(dir).TryGet("game.exe", "good morning.", out var t));
        Assert.Equal("Günaydın", t);
    }

    [Fact]
    public void Google_ParsesSegments()
    {
        var json = "[[[\"Merhaba \",\"Hello \",null,null,10],[\"dünya\",\"world\",null,null,10]],null,\"en\"]";
        Assert.Equal("Merhaba dünya", GoogleFreeTranslator.ParseResponse(json));
    }

    [Fact]
    public void Glossary_ProtectAndRestore()
    {
        var g = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Geralt"] = "Geralt", ["Kaer Morhen"] = "Kaer Morhen" };
        var p = GlossaryProtector.Protect("Geralt, go to Kaer Morhen.", g, out var map);
        Assert.DoesNotContain("Geralt", p);
        var restored = GlossaryProtector.Restore(p.Replace("go to", "git"), map);
        Assert.Equal("Geralt, git Kaer Morhen.", restored);
    }

    [Fact]
    public void GlossaryParser_ParsesLines()
    {
        var g = GlossaryParser.Parse("Geralt\nWild Hunt = Yabani Av\n# yorum\n\n Ciri = ");
        Assert.Equal("Geralt", g["geralt"]);
        Assert.Equal("Yabani Av", g["Wild Hunt"]);
        Assert.Equal("Ciri", g["Ciri"]);
        Assert.Equal(3, g.Count);
    }

    [Fact]
    public void Prompt_ContainsContextGlossaryAndText()
    {
        var p = PromptBuilder.User(new TranslationRequest("Run!", new[] { "They found us." }, new Dictionary<string, string> { ["Arthur"] = "Arthur" }, "rdr2.exe"));
        Assert.Contains("They found us.", p);
        Assert.Contains("Arthur => Arthur", p);
        Assert.EndsWith("TRANSLATE:\nRun!", p.Replace("\r\n", "\n"));
        Assert.Equal("Koş!", PromptBuilder.CleanOutput("Turkish: \"Koş!\""));
    }
}
