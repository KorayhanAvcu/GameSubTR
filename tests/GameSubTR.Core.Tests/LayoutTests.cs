using GameSubTR.Core.Models;
using Xunit;

namespace GameSubTR.Core.Tests;

public class LayoutTests
{
    [Fact]
    public void Default_IsTransparentAboveRegion()
    {
        var p = GameProfile.CreateDefault("game.exe");
        Assert.Equal(OverlayPlacement.AboveOriginal, p.Placement);
        Assert.Equal(0.0, p.BackgroundOpacity);
        p.Region = new NormalizedRect(0.2, 0.80, 0.6, 0.10);
        var l = LayoutCalculator.For(p);
        Assert.False(l.CoverBox);
        Assert.False(l.CenterInBox);
        // Kutunun alt kenarı seçilen alanın üst kenarında.
        Assert.InRange(l.Box.Y + l.Box.Height, 0.7999, 0.8001);
        Assert.InRange(l.Box.Y, 0.4999, 0.5001);
    }

    [Fact]
    public void RegionAtTop_PlacesBelow()
    {
        var p = GameProfile.CreateDefault("g.exe");
        p.Region = new NormalizedRect(0.1, 0.02, 0.8, 0.08);
        var l = LayoutCalculator.For(p);
        Assert.InRange(l.Box.Y, 0.0999, 0.1001);
    }

    [Fact]
    public void OverOriginal_CoversRegion()
    {
        var p = GameProfile.CreateDefault("g.exe");
        p.Placement = OverlayPlacement.OverOriginal;
        p.Region = new NormalizedRect(0.2, 0.8, 0.6, 0.1);
        var l = LayoutCalculator.For(p);
        Assert.True(l.CoverBox);
        Assert.Equal(p.Region, l.Box);
    }

    [Fact]
    public void Migration_V1ProfilesBecomeTransparent()
    {
        var s = new AppSettings { Version = 1 };
        var p = s.GetOrCreateProfile("old.exe");
        p.Placement = OverlayPlacement.OverOriginal;
        p.BackgroundOpacity = 0.7;
        SettingsStore.Migrate(s);
        Assert.Equal(OverlayPlacement.AboveOriginal, p.Placement);
        Assert.Equal(0.0, p.BackgroundOpacity);
        Assert.Equal(AppSettings.CurrentVersion, s.Version);
    }
}
