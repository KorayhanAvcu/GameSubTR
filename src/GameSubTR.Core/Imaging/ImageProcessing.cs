using GameSubTR.Core.Models;

namespace GameSubTR.Core.Imaging;

/// <summary>BGRA8 görüntü (satırlar sıkı paketli: Stride = Width * 4).</summary>
public sealed class BgraImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
    public int Stride => Width * 4;

    public BgraImage(int width, int height, byte[]? pixels = null)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        Width = width; Height = height;
        Pixels = pixels ?? new byte[width * height * 4];
        if (Pixels.Length < width * height * 4) throw new ArgumentException("Piksel dizisi küçük", nameof(pixels));
    }

    /// <summary>Kaynak tampondan (farklı stride ile) kopya oluşturur.</summary>
    public static BgraImage FromBuffer(ReadOnlySpan<byte> src, int width, int height, int srcStride)
    {
        var img = new BgraImage(width, height);
        int row = width * 4;
        for (int y = 0; y < height; y++)
            src.Slice(y * srcStride, row).CopyTo(img.Pixels.AsSpan(y * row, row));
        return img;
    }

    /// <summary>Alt bölge kopyası.</summary>
    public BgraImage Crop(int x, int y, int w, int h)
    {
        x = Math.Clamp(x, 0, Width - 1); y = Math.Clamp(y, 0, Height - 1);
        w = Math.Clamp(w, 1, Width - x); h = Math.Clamp(h, 1, Height - y);
        var img = new BgraImage(w, h);
        for (int r = 0; r < h; r++)
            Pixels.AsSpan(((y + r) * Width + x) * 4, w * 4).CopyTo(img.Pixels.AsSpan(r * w * 4, w * 4));
        return img;
    }
}

/// <summary>256 bitlik algısal özet; ardışık karelerde değişim tespiti için.</summary>
public readonly record struct ImageHash(ulong A, ulong B, ulong C, ulong D)
{
    public int Distance(ImageHash o) =>
        System.Numerics.BitOperations.PopCount(A ^ o.A) + System.Numerics.BitOperations.PopCount(B ^ o.B) +
        System.Numerics.BitOperations.PopCount(C ^ o.C) + System.Numerics.BitOperations.PopCount(D ^ o.D);
}

public sealed record PreprocessResult(BgraImage Image, ImageHash Hash, double ForegroundRatio, bool Binarized);

/// <summary>
/// OCR öncesi görüntü hazırlama: büyütme + renk ayrıştırma + Otsu eşikleme.
/// Çıktı beyaz zemin üzerinde siyah metindir (OCR motorları en iyi bunu okur).
/// </summary>
public static class ImagePreprocessor
{
    private const int Pad = 12;

    public static int ChooseScale(int height) => height < 90 ? 3 : height < 220 ? 2 : 1;

    public static PreprocessResult Process(BgraImage src, OcrColorFilter filter, int? forcedScale = null)
    {
        int scale = forcedScale ?? ChooseScale(src.Height);
        // Çok büyük bölgeleri gereksiz büyütme.
        while (scale > 1 && (long)src.Width * scale * src.Height * scale > 6_000_000) scale--;

        var textness = ComputeTextness(src, filter);
        var hash = ComputeHash(textness, src.Width, src.Height);

        if (filter == OcrColorFilter.None)
            return new(ToBgra(Upscale(textness, src.Width, src.Height, scale, out int w0, out int h0), w0, h0, invert: false), hash, 0, false);

        int threshold = Math.Max(Otsu(textness), filter == OcrColorFilter.Auto ? 150 : 130);
        int fg = 0;
        foreach (var v in textness) if (v >= threshold) fg++;
        double ratio = (double)fg / textness.Length;

        var up = Upscale(textness, src.Width, src.Height, scale, out int w, out int h);
        if (ratio > 0.40 || ratio < 0.0005)
        {
            // Parlak arka plan ya da metin yok: eşiklemeden gri tonla devam et.
            var gray = ComputeTextness(src, OcrColorFilter.None);
            var upGray = Upscale(gray, src.Width, src.Height, scale, out w, out h);
            return new(ToBgra(upGray, w, h, invert: false), hash, ratio, false);
        }

        var bin = new byte[up.Length];
        for (int i = 0; i < up.Length; i++) bin[i] = up[i] >= threshold ? (byte)0 : (byte)255; // metin siyah
        return new(ToBgra(bin, w, h, invert: false), hash, ratio, true);
    }

    /// <summary>Piksel başına "metin olma" değeri (0..255).</summary>
    public static byte[] ComputeTextness(BgraImage img, OcrColorFilter filter)
    {
        var p = img.Pixels;
        var t = new byte[img.Width * img.Height];
        for (int i = 0, j = 0; i < t.Length; i++, j += 4)
        {
            int b = p[j], g = p[j + 1], r = p[j + 2];
            t[i] = filter switch
            {
                OcrColorFilter.White => (byte)Math.Min(r, Math.Min(g, b)),
                OcrColorFilter.Yellow => (byte)Math.Max(0, Math.Min(r, g) - Math.Max(0, b - 140) / 2),
                OcrColorFilter.Auto => (byte)Math.Max(r, Math.Max(g, b)),
                _ => (byte)((r * 77 + g * 150 + b * 29) >> 8)
            };
        }
        return t;
    }

    public static int Otsu(byte[] data)
    {
        Span<int> hist = stackalloc int[256];
        foreach (var v in data) hist[v]++;
        long total = data.Length, sum = 0;
        for (int i = 0; i < 256; i++) sum += (long)i * hist[i];
        long sumB = 0, wB = 0; double best = -1; int thr = 128;
        for (int i = 0; i < 256; i++)
        {
            wB += hist[i]; if (wB == 0) continue;
            long wF = total - wB; if (wF == 0) break;
            sumB += (long)i * hist[i];
            double mB = (double)sumB / wB, mF = (double)(sum - sumB) / wF;
            double between = (double)wB * wF * (mB - mF) * (mB - mF);
            if (between > best) { best = between; thr = i + 1; }
        }
        return thr;
    }

    /// <summary>Bilinear büyütme + kenar boşluğu (arka plan = 0 → çıktıda beyaz olur).</summary>
    public static byte[] Upscale(byte[] src, int w, int h, int scale, out int outW, out int outH)
    {
        int sw = w * scale, sh = h * scale;
        outW = sw + Pad * 2; outH = sh + Pad * 2;
        var dst = new byte[outW * outH];
        for (int y = 0; y < sh; y++)
        {
            double fy = (y + 0.5) / scale - 0.5;
            int y0 = Math.Clamp((int)Math.Floor(fy), 0, h - 1), y1 = Math.Min(y0 + 1, h - 1);
            double ty = Math.Clamp(fy - y0, 0, 1);
            for (int x = 0; x < sw; x++)
            {
                double fx = (x + 0.5) / scale - 0.5;
                int x0 = Math.Clamp((int)Math.Floor(fx), 0, w - 1), x1 = Math.Min(x0 + 1, w - 1);
                double tx = Math.Clamp(fx - x0, 0, 1);
                double v = src[y0 * w + x0] * (1 - tx) * (1 - ty) + src[y0 * w + x1] * tx * (1 - ty)
                         + src[y1 * w + x0] * (1 - tx) * ty + src[y1 * w + x1] * tx * ty;
                dst[(y + Pad) * outW + x + Pad] = (byte)Math.Round(v);
            }
        }
        return dst;
    }

    private static BgraImage ToBgra(byte[] gray, int w, int h, bool invert)
    {
        var img = new BgraImage(w, h);
        var p = img.Pixels;
        for (int i = 0, j = 0; i < gray.Length; i++, j += 4)
        {
            byte v = invert ? (byte)(255 - gray[i]) : gray[i];
            p[j] = v; p[j + 1] = v; p[j + 2] = v; p[j + 3] = 255;
        }
        return img;
    }

    public static ImageHash ComputeHash(byte[] t, int w, int h)
    {
        const int gw = 32, gh = 8;
        Span<double> cells = stackalloc double[gw * gh];
        for (int gy = 0; gy < gh; gy++)
        for (int gx = 0; gx < gw; gx++)
        {
            int x0 = gx * w / gw, x1 = Math.Max(x0 + 1, (gx + 1) * w / gw);
            int y0 = gy * h / gh, y1 = Math.Max(y0 + 1, (gy + 1) * h / gh);
            long s = 0; int n = 0;
            for (int y = y0; y < y1 && y < h; y++)
            for (int x = x0; x < x1 && x < w; x++) { s += t[y * w + x]; n++; }
            cells[gy * gw + gx] = n == 0 ? 0 : (double)s / n;
        }
        double mean = 0; foreach (var c in cells) mean += c; mean /= cells.Length;
        Span<ulong> bits = stackalloc ulong[4];
        for (int i = 0; i < 256; i++)
            if (cells[i] > mean + 4) bits[i >> 6] |= 1UL << (i & 63);
        return new ImageHash(bits[0], bits[1], bits[2], bits[3]);
    }
}
