using System.Runtime.InteropServices.WindowsRuntime;
using GameSubTR.Core.Imaging;
using GameSubTR.Core.Text;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace GameSubTR.App.Ocr;

/// <summary>Windows'un yerleşik OCR motoru (Windows.Media.Ocr) — hızlı, ücretsiz, çevrimdışı.</summary>
public sealed class WindowsOcr
{
    private readonly OcrEngine _engine;

    private WindowsOcr(OcrEngine engine) => _engine = engine;

    public string Language => _engine.RecognizerLanguage.LanguageTag;

    /// <summary>İngilizce OCR motoru oluşturur. İngilizce OCR paketi yüklü değilse null döner.</summary>
    public static WindowsOcr? TryCreate(out string? error)
    {
        error = null;
        foreach (var tag in new[] { "en-US", "en-GB", "en" })
        {
            var lang = new Windows.Globalization.Language(tag);
            if (OcrEngine.IsLanguageSupported(lang))
            {
                var e = OcrEngine.TryCreateFromLanguage(lang);
                if (e != null) return new WindowsOcr(e);
            }
        }
        var any = OcrEngine.TryCreateFromUserProfileLanguages();
        if (any != null && any.RecognizerLanguage.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            return new WindowsOcr(any);

        error = "İngilizce OCR paketi yüklü değil. PowerShell'i yönetici olarak açıp şunu çalıştırın:\n" +
                "Add-WindowsCapability -Online -Name \"Language.OCR~~~en-US~0.0.1.0\"";
        return null;
    }

    public async Task<string> RecognizeAsync(BgraImage image)
    {
        int max = (int)OcrEngine.MaxImageDimension;
        if (image.Width > max || image.Height > max)
            image = image.Crop(0, 0, Math.Min(image.Width, max), Math.Min(image.Height, max));

        using var bmp = SoftwareBitmap.CreateCopyFromBuffer(image.Pixels.AsBuffer(), BitmapPixelFormat.Bgra8,
            image.Width, image.Height, BitmapAlphaMode.Premultiplied);
        var result = await _engine.RecognizeAsync(bmp);
        var lines = result.Lines.Select(l => TextUtil.CleanOcr(l.Text)).Where(l => l.Length > 0);
        return TextUtil.JoinOcrLines(lines);
    }
}
