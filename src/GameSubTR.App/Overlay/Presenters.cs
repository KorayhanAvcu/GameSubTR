using GameSubTR.Core.Models;

namespace GameSubTR.App.Overlay;

/// <summary>Altyazı yerleşimi: normalize hedef kutu + hizalama.</summary>
public sealed record SubtitleLayout(NormalizedRect Box, bool CenterInBox, bool CoverBox);

public sealed record SubtitleStyle(double FontSize, string TextColor, double BackgroundOpacity)
{
    public static SubtitleStyle From(GameProfile p) =>
        new(p.FontSize, p.TextColor,
            p.Placement == OverlayPlacement.OverOriginal ? Math.Max(p.BackgroundOpacity, 0.88) : p.BackgroundOpacity);
}

/// <summary>Çeviriyi ekranda gösteren katman (pencere veya oyun içi hook).</summary>
public interface ISubtitlePresenter : IDisposable
{
    string Name { get; }
    bool IsAlive { get; }
    void Show(string text, string? original, SubtitleLayout layout, SubtitleStyle style);
    void Clear();
    /// <summary>Kısa durum/ipucu mesajı (ör. "Çeviri açık").</summary>
    void ShowStatus(string message, TimeSpan duration);
}

public static class LayoutCalculator
{
    public static SubtitleLayout For(GameProfile p)
    {
        if (p.Placement == OverlayPlacement.OverOriginal && p.Region is { IsValid: true } r)
            return new SubtitleLayout(r, CenterInBox: true, CoverBox: true);
        // Sabit konum: ekranın alt kısmında, %80 genişlik.
        double bottom = Math.Clamp(p.FixedBottom, 0.2, 1.0);
        return new SubtitleLayout(new NormalizedRect(0.1, Math.Max(0, bottom - 0.25), 0.8, Math.Min(0.25, bottom)), CenterInBox: false, CoverBox: false);
    }
}
