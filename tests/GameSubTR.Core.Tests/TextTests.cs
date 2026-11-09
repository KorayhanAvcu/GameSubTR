using GameSubTR.Core.Text;
using Xunit;

namespace GameSubTR.Core.Tests;

public class TextTests
{
    [Fact]
    public void CleanOcr_FixesPipeAndWhitespace()
    {
        Assert.Equal("I know what I saw.", TextUtil.CleanOcr("  |  know   what | saw.  "));
    }

    [Fact]
    public void CleanOcr_StripsEdgeJunk()
    {
        Assert.Equal("Where are you going?", TextUtil.CleanOcr("~• Where are you going? ¦_"));
    }

    [Fact]
    public void Similarity_IgnoresPunctuationAndCase()
    {
        Assert.Equal(1.0, TextUtil.Similarity("Hello, there!", "hello there"));
        Assert.True(TextUtil.Similarity("We have to go now", "We have to go") > 0.7);
        Assert.True(TextUtil.Similarity("Completely different", "Nothing alike here") < 0.5);
    }

    [Fact]
    public void JoinOcrLines_KeepsSpeakerLines()
    {
        var joined = TextUtil.JoinOcrLines(new[] { "- Are you sure", "about this?", "- Yes." });
        Assert.Equal("- Are you sure about this?\n- Yes.", joined);
        Assert.Equal("ARTHUR: Get in\nJOHN: No", TextUtil.JoinOcrLines(new[] { "ARTHUR: Get in", "JOHN: No" }));
        Assert.Equal("This is one long sentence", TextUtil.JoinOcrLines(new[] { "This is one", "long sentence" }));
    }

    [Fact]
    public void Stabilizer_WaitsForRepeat_ThenPublishesOnce()
    {
        var s = new TextStabilizer();
        Assert.Equal(StabilizerEventKind.None, s.Push("Hello there").Kind);
        var e = s.Push("Hello there");
        Assert.Equal(StabilizerEventKind.NewText, e.Kind);
        Assert.Equal("Hello there", e.Text);
        Assert.Equal(StabilizerEventKind.None, s.Push("Hello there").Kind);
        Assert.Equal(StabilizerEventKind.None, s.Push("Hello there.").Kind); // OCR gürültüsü → aynı
    }

    [Fact]
    public void Stabilizer_TypewriterEffect_PublishesOnlyFinal()
    {
        var s = new TextStabilizer();
        var published = new List<string>();
        foreach (var t in new[] { "We", "We need", "We need to leave", "We need to leave before dawn", "We need to leave before dawn", "We need to leave before dawn" })
        {
            var e = s.Push(t);
            if (e.Kind == StabilizerEventKind.NewText) published.Add(e.Text);
        }
        Assert.Single(published);
        Assert.Equal("We need to leave before dawn", published[0]);
    }

    [Fact]
    public void Stabilizer_ClearsAfterEmptyFrames()
    {
        var s = new TextStabilizer();
        s.Push("Line one"); s.Push("Line one");
        Assert.Equal(StabilizerEventKind.None, s.Push("").Kind);
        Assert.Equal(StabilizerEventKind.Cleared, s.Push("").Kind);
        Assert.Equal(StabilizerEventKind.None, s.Push("").Kind);
    }
}
