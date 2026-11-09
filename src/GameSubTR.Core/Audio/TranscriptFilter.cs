using System.Text.RegularExpressions;

namespace GameSubTR.Core.Audio;

/// <summary>Whisper'ın sessizlik/müzik üzerindeki bilinen "halüsinasyon" çıktılarını eler.</summary>
public static partial class TranscriptFilter
{
    private static readonly HashSet<string> Junk = new(StringComparer.OrdinalIgnoreCase)
    {
        "thank you", "thank you.", "thanks for watching", "thanks for watching!", "thank you for watching",
        "thank you for watching.", "you", "bye", "bye.", "subtitles by the amara.org community",
        "please subscribe", "like and subscribe", "so", "okay", "uh", "um", "hmm", "mm-hmm", "oh", "ah", "...", "."
    };

    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)|\*[^*]*\*|♪[^♪]*♪?")] private static partial Regex Bracketed();
    [GeneratedRegex(@"\s+")] private static partial Regex Ws();

    public static string Clean(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var s = Bracketed().Replace(text, " ");
        s = s.Replace("♪", " ");
        s = Ws().Replace(s, " ").Trim();
        if (s.Length == 0) return "";
        var bare = s.Trim().TrimEnd('.', '!', '?', ',').Trim();
        if (Junk.Contains(s) || Junk.Contains(bare)) return "";
        if (Text.TextUtil.LetterCount(s) < 2) return "";
        if (IsRepetitive(s)) return "";
        return s;
    }

    /// <summary>"you you you you" gibi döngüye girmiş çıktıları tespit eder.</summary>
    private static bool IsRepetitive(string s)
    {
        var words = s.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 6) return false;
        var distinct = words.Distinct().Count();
        return distinct <= words.Length / 4;
    }
}
