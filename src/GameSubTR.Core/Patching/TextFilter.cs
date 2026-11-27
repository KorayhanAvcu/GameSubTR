using System.Text;
using System.Text.RegularExpressions;

namespace GameSubTR.Core.Patching;

/// <summary>
/// Bir dizenin oyuncuya gösterilen İngilizce metin mi, yoksa kimlik/yol/sayı gibi
/// teknik bir değer mi olduğunu tahmin eder. Teknik değerleri çevirmek oyunu bozabilir.
/// </summary>
public static partial class TextFilter
{
    [GeneratedRegex(@"^(https?://|www\.|[a-z]:\\|/)", RegexOptions.IgnoreCase)] private static partial Regex PathLike();
    [GeneratedRegex(@"^#?[0-9a-f]{6}([0-9a-f]{2})?$", RegexOptions.IgnoreCase)] private static partial Regex HexColor();
    [GeneratedRegex(@"\.(png|jpe?g|dds|tga|wav|ogg|mp3|json|xml|txt|csv|prefab|mat|asset|uasset|fbx|lua|cs|dll|exe|ini|bnk)$", RegexOptions.IgnoreCase)] private static partial Regex FileName();
    [GeneratedRegex(@"^[a-z0-9]+([_.\-][a-z0-9]+)+$", RegexOptions.IgnoreCase)] private static partial Regex SnakeOrDotted();
    [GeneratedRegex(@"^[a-z]+([A-Z][a-z0-9]*)+$")] private static partial Regex CamelCase();
    [GeneratedRegex(@"^[A-Z0-9_]+$")] private static partial Regex UpperConst();

    public static bool IsTranslatable(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = s.Trim();
        if (t.Length < 2 || t.Length > 4000) return false;

        int letters = 0, asciiLetters = 0;
        foreach (var c in t)
        {
            if (char.IsLetter(c)) { letters++; if (c < 128) asciiLetters++; }
        }
        if (letters < 2) return false;
        if (asciiLetters < letters * 0.8) return false; // İngilizce değil (zaten başka dil)

        if (PathLike().IsMatch(t) || HexColor().IsMatch(t) || FileName().IsMatch(t)) return false;
        if (Guid.TryParse(t, out _)) return false;

        bool hasSpace = t.Contains(' ');
        if (!hasSpace)
        {
            // Tek kelime: "Inventory", "Options", "OK" geçer; "idle_anim", "playerName", "MAX_HP", "true" geçmez.
            if (SnakeOrDotted().IsMatch(t) || CamelCase().IsMatch(t)) return false;
            if (UpperConst().IsMatch(t) && t.Contains('_')) return false;
            if (t is "true" or "false" or "null" or "none" or "default") return false;
            if (char.IsLower(t[0]) && t.All(char.IsLetter)) return false; // anahtar gibi küçük harf tek kelime
            if (t.Any(char.IsDigit) && !t.Any(char.IsWhiteSpace) && letters < t.Length / 2) return false;
        }
        return true;
    }
}

/// <summary>Kod parçaları ({0}, %s, &lt;b&gt;, [tag], \n) — çeviride korunmaları gerekir.</summary>
public static partial class Placeholders
{
    [GeneratedRegex(@"\{[^{}\s]{0,40}\}|%(\d+\$)?[-+ 0#]*\d*(\.\d+)?[sdifuxXc%]|<[^<>]{1,80}>|\[[^\[\]]{1,40}\]|\$\{[^}]+\}|\\[nrt]")]
    public static partial Regex Pattern();

    public static List<string> Extract(string s) => Pattern().Matches(s).Select(m => m.Value).ToList();

    /// <summary>Çeviride orijinaldeki kod parçalarının hepsi (aynı sayıda) var mı?</summary>
    public static bool Preserved(string original, string translated)
    {
        var a = Extract(original);
        if (a.Count == 0) return true;
        var b = Extract(translated);
        var counts = new Dictionary<string, int>();
        foreach (var x in a) counts[x] = counts.GetValueOrDefault(x) + 1;
        foreach (var x in b) if (counts.ContainsKey(x)) counts[x]--;
        return counts.Values.All(v => v <= 0);
    }

    /// <summary>Google Translate'in kod parçalarına eklediği boşlukları onarmayı dener: "{ 0 }" → "{0}", "< b >" → "&lt;b&gt;".</summary>
    public static string Repair(string original, string translated)
    {
        var s = translated;
        s = Regex.Replace(s, @"\{\s*([^{}]*?)\s*\}", m => "{" + m.Groups[1].Value.Replace(" ", "") + "}");
        s = Regex.Replace(s, @"<\s*(/?)\s*([^<>]*?)\s*>", m => "<" + m.Groups[1].Value + m.Groups[2].Value + ">");
        s = Regex.Replace(s, @"%\s+([sdif])", "%$1");
        s = Regex.Replace(s, @"\\\s+([nrt])", @"\$1");
        // Orijinalde bitişik olan kod parçaları arasına eklenen boşlukları kaldır: "<b> {name} </b>" → "<b>{name}</b>".
        var ms = Pattern().Matches(original);
        for (int k = 0; k + 1 < ms.Count; k++)
        {
            if (ms[k].Index + ms[k].Length != ms[k + 1].Index) continue;
            s = Regex.Replace(s, Regex.Escape(ms[k].Value) + @"\s+" + Regex.Escape(ms[k + 1].Value), ms[k].Value + ms[k + 1].Value);
        }
        // Google bazen %S / {Name} içini çevirir ya da büyük harf yapar: orijinaldekiyle büyük/küçük harf duyarsız eşleşeni geri koy.
        foreach (var ph in Extract(original))
        {
            if (s.Contains(ph, StringComparison.Ordinal)) continue;
            int i = s.IndexOf(ph, StringComparison.OrdinalIgnoreCase);
            if (i >= 0) s = s[..i] + ph + s[(i + ph.Length)..];
        }
        return s;
    }
}

/// <summary>Dosya kodlamasını (BOM) korumak için yardımcılar.</summary>
public static class TextFile
{
    public static (string Text, Encoding Encoding) Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Encoding enc;
        int skip = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) { enc = new UTF8Encoding(true); skip = 3; }
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) { enc = new UnicodeEncoding(false, true); skip = 2; }
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) { enc = new UnicodeEncoding(true, true); skip = 2; }
        else enc = new UTF8Encoding(false);
        return (enc.GetString(bytes, skip, bytes.Length - skip), enc);
    }

    public static void Write(string path, string text, Encoding enc)
    {
        var tmp = path + ".gstr.tmp";
        File.WriteAllText(tmp, text, enc); // UTF8Encoding(true) BOM yazar, (false) yazmaz
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>İkili dosyaları atlamak için hızlı kontrol (NUL baytı).</summary>
    public static bool LooksBinary(string path)
    {
        using var fs = File.OpenRead(path);
        var buf = new byte[Math.Min(8192, (int)Math.Max(0, fs.Length))];
        int n = fs.Read(buf, 0, buf.Length);
        if (n >= 2 && ((buf[0] == 0xFF && buf[1] == 0xFE) || (buf[0] == 0xFE && buf[1] == 0xFF))) return false; // UTF-16
        for (int i = 0; i < n; i++) if (buf[i] == 0) return true;
        return false;
    }
}
