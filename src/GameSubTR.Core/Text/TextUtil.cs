using System.Text;
using System.Text.RegularExpressions;

namespace GameSubTR.Core.Text;

public static partial class TextUtil
{
    [GeneratedRegex(@"\s+")] private static partial Regex Ws();
    [GeneratedRegex(@"(?<=\p{L})\|(?=\p{L}|\s|$)|(?<=^|\s)\|(?=\p{L}|\s|$)")] private static partial Regex PipeAsI();
    [GeneratedRegex(@"^[^\p{L}\p{N}""'(\[-]+|[^\p{L}\p{N}.!?""')\]…]+$")] private static partial Regex EdgeJunk();

    /// <summary>OCR çıktısını temizler: boşlukları birleştirir, sık OCR hatalarını düzeltir.</summary>
    public static string CleanOcr(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw.Replace('’', '\'').Replace('‘', '\'').Replace('“', '"').Replace('”', '"');
        s = PipeAsI().Replace(s, "I");
        s = Ws().Replace(s, " ").Trim();
        s = EdgeJunk().Replace(s, "").Trim();
        return s;
    }

    /// <summary>Karşılaştırma için anahtar: küçük harf, sadece harf/rakam.</summary>
    public static string Key(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    public static int LetterCount(string s)
    {
        int n = 0;
        foreach (var c in s) if (char.IsLetter(c)) n++;
        return n;
    }

    /// <summary>0..1 benzerlik (normalize Levenshtein, harf/rakam anahtarı üzerinden).</summary>
    public static double Similarity(string a, string b)
    {
        var x = Key(a); var y = Key(b);
        if (x.Length == 0 && y.Length == 0) return 1;
        if (x.Length == 0 || y.Length == 0) return 0;
        int d = Levenshtein(x, y);
        return 1.0 - (double)d / Math.Max(x.Length, y.Length);
    }

    public static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    /// <summary>Konuşmacı satırlarını korur ("- Hey" / "NAME: ..."), diğer satırları tek cümlede birleştirir.</summary>
    public static string JoinOcrLines(IEnumerable<string> lines)
    {
        var result = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            bool newSpeaker = line.StartsWith('-') || line.StartsWith('–') || SpeakerPrefix().IsMatch(line);
            if (result.Count == 0 || newSpeaker) result.Add(line);
            else result[^1] = result[^1] + " " + line;
        }
        return string.Join("\n", result);
    }

    [GeneratedRegex(@"^[A-Z][A-Za-z .'-]{1,24}:\s")] private static partial Regex SpeakerPrefix();
}
