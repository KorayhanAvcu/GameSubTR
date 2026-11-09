namespace GameSubTR.Core.Models;

/// <summary>Altyazı yerleşimi: normalize hedef kutu + hizalama.</summary>
public sealed record SubtitleLayout(NormalizedRect Box, bool CenterInBox, bool CoverBox);

public static class LayoutCalculator
{
    /// <summary>Saydam modda çevirinin kapladığı en fazla yükseklik (ekranın oranı).</summary>
    public const double AboveSpace = 0.30;

    public static SubtitleLayout For(GameProfile p)
    {
        if (p.Region is { IsValid: true } r)
        {
            switch (p.Placement)
            {
                case OverlayPlacement.OverOriginal:
                    return new SubtitleLayout(r, CenterInBox: true, CoverBox: true);
                case OverlayPlacement.AboveOriginal:
                    if (r.Y >= 0.08)
                    {
                        // Alt kenarı seçilen alanın üst kenarına hizalı.
                        double h = Math.Min(AboveSpace, r.Y);
                        return new SubtitleLayout(new NormalizedRect(r.X, r.Y - h, r.Width, h), CenterInBox: false, CoverBox: false);
                    }
                    // Alan ekranın en üstündeyse altına koy.
                    double top = Math.Min(1, r.Y + r.Height);
                    double hb = Math.Min(AboveSpace, 1 - top);
                    return new SubtitleLayout(new NormalizedRect(r.X, top, r.Width, Math.Max(0.01, hb)), CenterInBox: false, CoverBox: false);
            }
        }
        // Sabit konum (ya da bölge yok): ekranın alt kısmında, %80 genişlik.
        double bottom = Math.Clamp(p.FixedBottom, 0.2, 1.0);
        return new SubtitleLayout(new NormalizedRect(0.1, Math.Max(0, bottom - 0.25), 0.8, Math.Min(0.25, bottom)), CenterInBox: false, CoverBox: false);
    }
}
