using GameSubTR.Core.Models;

namespace GameSubTR.App.Overlay;

public sealed record SubtitleStyle(double FontSize, string TextColor, double BackgroundOpacity)
{
    public static SubtitleStyle From(GameProfile p) =>
        new(p.FontSize, p.TextColor,
            p.Placement == OverlayPlacement.OverOriginal ? Math.Max(p.BackgroundOpacity, 0.88) : Math.Clamp(p.BackgroundOpacity, 0, 1));
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
