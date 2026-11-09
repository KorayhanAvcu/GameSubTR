using GameSubTR.Core.Imaging;
using GameSubTR.Core.Models;
using Xunit;

namespace GameSubTR.Core.Tests;

public class ImagingTests
{
    /// <summary>Koyu, gürültülü arka plan üzerine beyaz "metin" çubukları çizer.</summary>
    private static BgraImage MakeSubtitleLike(int w, int h, bool bright = false)
    {
        var img = new BgraImage(w, h);
        var rnd = new Random(1);
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4;
            byte bg = bright ? (byte)(200 + rnd.Next(40)) : (byte)(20 + rnd.Next(60));
            bool text = !bright && y > h / 3 && y < 2 * h / 3 && (x / 6) % 3 != 0 && x > w / 5 && x < 4 * w / 5;
            byte v = text ? (byte)245 : bg;
            img.Pixels[i] = v; img.Pixels[i + 1] = v; img.Pixels[i + 2] = v; img.Pixels[i + 3] = 255;
        }
        return img;
    }

    [Fact]
    public void Process_BinarizesWhiteTextToBlackOnWhite()
    {
        var src = MakeSubtitleLike(300, 60);
        var r = ImagePreprocessor.Process(src, OcrColorFilter.Auto);
        Assert.True(r.Binarized);
        Assert.Equal(300 * 3 + 24, r.Image.Width); // 3x büyütme + 12px kenar boşluğu
        // Köşe (kenar boşluğu) beyaz, metin bölgesinin ortası siyah olmalı.
        Assert.Equal(255, r.Image.Pixels[0]);
        int cx = r.Image.Width / 2 + 3, cy = r.Image.Height / 2;
        bool anyBlack = false;
        for (int dx = -9; dx <= 9; dx++)
            if (r.Image.Pixels[(cy * r.Image.Width + cx + dx) * 4] == 0) anyBlack = true;
        Assert.True(anyBlack);
    }

    [Fact]
    public void Process_FallsBackToGray_OnBrightBackground()
    {
        var r = ImagePreprocessor.Process(MakeSubtitleLike(200, 50, bright: true), OcrColorFilter.Auto);
        Assert.False(r.Binarized);
    }

    [Fact]
    public void Hash_SameImageZeroDistance_DifferentImageLarge()
    {
        var a = ImagePreprocessor.Process(MakeSubtitleLike(300, 60), OcrColorFilter.Auto).Hash;
        var b = ImagePreprocessor.Process(MakeSubtitleLike(300, 60), OcrColorFilter.Auto).Hash;
        var c = ImagePreprocessor.Process(new BgraImage(300, 60), OcrColorFilter.Auto).Hash;
        Assert.Equal(0, a.Distance(b));
        Assert.True(a.Distance(c) > 20);
    }

    [Fact]
    public void Otsu_SplitsBimodal()
    {
        var data = new byte[1000];
        for (int i = 0; i < 1000; i++) data[i] = i < 500 ? (byte)30 : (byte)220;
        int t = ImagePreprocessor.Otsu(data);
        Assert.InRange(t, 31, 220);
    }

    [Fact]
    public void NormalizedRect_RoundTrip()
    {
        var r = NormalizedRect.FromPixels(480, 900, 960, 120, 1920, 1080);
        Assert.Equal((480, 900, 960, 120), r.ToPixels(1920, 1080));
        Assert.Equal((240, 450, 480, 60), r.ToPixels(960, 540));
        Assert.True(r.IsValid);
    }
}
