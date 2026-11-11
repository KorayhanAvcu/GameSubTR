using System.Text;
using System.Text.RegularExpressions;

namespace GameSubTR.Patching;

public sealed record MaskedText(string Text, IReadOnlyList<string> Tokens);

/// <summary>
/// Oyun kodlarını (değişken, etiket, kontrol kodu, satır sonu) çeviri motorundan korur:
/// çeviri öncesi &lt;x0/&gt; biçiminde maskeler, sonra geri koyar ve hiçbirinin kaybolmadığını doğrular.
/// </summary>
public static partial class PlaceholderGuard
{
    private const string Common = @"\r\n|\n|%\d+|%[sd]|\{\d+\}";

    [GeneratedRegex(@"\[\[|\[[^\[\]\r\n]+\]|\{[^{}\r\n]*\}|%\([^)\r\n]+\)[sd]|" + Common)]
    private static partial Regex RenPyRegex();

    [GeneratedRegex(@"\\[A-Za-z]+\[[^\]\r\n]*\]|\\[.|!><^${}\\]|\\G|" + Common)]
    private static partial Regex RpgMakerRegex();

    [GeneratedRegex(Common)]
    private static partial Regex CommonRegex();

    [GeneratedRegex(@"<\s*x\s*(\d+)\s*/\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"\.(ogg|png|mp3|jpg|jpeg|webp|m4a|wav|rpy|json)$", RegexOptions.IgnoreCase)]
    private static partial Regex FileNameRegex();

    private static Regex For(EngineKind engine) => engine switch
    {
        EngineKind.RenPy => RenPyRegex(),
        EngineKind.RpgMakerMV or EngineKind.RpgMakerMZ => RpgMakerRegex(),
        _ => CommonRegex()
    };

    public static MaskedText Mask(string source, EngineKind engine)
    {
        var tokens = new List<string>();
        var text = For(engine).Replace(source, m =>
        {
            tokens.Add(m.Value);
            return $"<x{tokens.Count - 1}/>";
        });
        return new MaskedText(text, tokens);
    }

    /// <summary>Çevirideki işaretleri özgün kodlarla değiştirir. Her kod tam bir kez geçmiyorsa false.</summary>
    public static bool TryRestore(string translated, MaskedText masked, out string result)
    {
        var seen = new int[masked.Tokens.Count];
        var ok = true;
        result = TokenRegex().Replace(translated, m =>
        {
            if (!int.TryParse(m.Groups[1].Value, out var i) || i < 0 || i >= seen.Length) { ok = false; return ""; }
            seen[i]++;
            return masked.Tokens[i];
        });
        if (!ok || seen.Any(c => c != 1)) { result = ""; return false; }
        return true;
    }

    /// <summary>Çevrilecek gerçek metin var mı (en az iki Latin harfi; dosya adı değil).</summary>
    public static bool IsTranslatable(string source, EngineKind engine)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;
        var trimmed = source.Trim();
        if (!trimmed.Any(char.IsWhiteSpace) && FileNameRegex().IsMatch(trimmed)) return false;
        var plain = TokenRegex().Replace(Mask(source, engine).Text, " ");
        var letters = 0;
        foreach (var c in plain)
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')) letters++;
        return letters >= 2;
    }

    /// <summary>Kodlar hariç görünen karakter sayısı (satır sarma için).</summary>
    public static int VisibleLength(string text, EngineKind engine) =>
        new StringBuilder(For(engine).Replace(text, "")).Length;
}
