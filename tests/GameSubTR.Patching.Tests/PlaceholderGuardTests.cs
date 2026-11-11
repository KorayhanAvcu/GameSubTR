using Xunit;

namespace GameSubTR.Patching.Tests;

public class PlaceholderGuardTests
{
    [Fact]
    public void Mask_RenPy()
    {
        var m = PlaceholderGuard.Mask("Hi [player], {b}run{/b}!\nNow", EngineKind.RenPy);
        Assert.Equal("Hi <x0/>, <x1/>run<x2/>!<x3/>Now", m.Text);
        Assert.Equal(new[] { "[player]", "{b}", "{/b}", "\n" }, m.Tokens);
    }

    [Fact]
    public void Mask_RpgMaker()
    {
        var m = PlaceholderGuard.Mask(@"\N[1]: Take \C[2]this\C[0]\.", EngineKind.RpgMakerMV);
        Assert.Equal(4, m.Tokens.Count);
        Assert.DoesNotContain('\\', m.Text);
        Assert.Equal("<x0/>: Take <x1/>this<x2/><x3/>", m.Text);
    }

    [Fact]
    public void Mask_CommonFormatSpecifiers()
    {
        var m = PlaceholderGuard.Mask("There was no effect on %1!", EngineKind.RpgMakerMZ);
        Assert.Equal(new[] { "%1" }, m.Tokens);
    }

    [Fact]
    public void Restore_AllowsReorderAndSpaces()
    {
        var m = PlaceholderGuard.Mask("Hi [player], {b}run{/b}!\nNow", EngineKind.RenPy);
        Assert.True(PlaceholderGuard.TryRestore("< x1 / >koş<x2/>, <x0/>!<X3/>Şimdi", m, out var r));
        Assert.Equal("{b}koş{/b}, [player]!\nŞimdi", r);
    }

    [Fact]
    public void Restore_FailsWhenTokenMissing()
    {
        var m = PlaceholderGuard.Mask("Hi [player]!", EngineKind.RenPy);
        Assert.False(PlaceholderGuard.TryRestore("Selam!", m, out _));
    }

    [Fact]
    public void Restore_FailsWhenTokenDuplicatedOrUnknown()
    {
        var m = PlaceholderGuard.Mask("Hi [player]!", EngineKind.RenPy);
        Assert.False(PlaceholderGuard.TryRestore("Selam <x0/> <x0/>!", m, out _));
        Assert.False(PlaceholderGuard.TryRestore("Selam <x0/> <x7/>!", m, out _));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("123", false)]
    [InlineData("[name]", false)]
    [InlineData("bgm_01.ogg", false)]
    [InlineData("...", false)]
    [InlineData("Go", true)]
    [InlineData("Hello [name]", true)]
    [InlineData("Read the notes.json file", true)]
    public void IsTranslatable_RejectsNonText(string s, bool expected) =>
        Assert.Equal(expected, PlaceholderGuard.IsTranslatable(s, EngineKind.RenPy));
}
