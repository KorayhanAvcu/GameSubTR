using System.Text;
using System.Text.RegularExpressions;

namespace GameSubTR.Patching.RenPy;

/// <summary>
/// .rpy betiklerinden çevrilecek metinleri çıkarır: diyalog/anlatım, menü seçenekleri,
/// _("...") dizgeleri ve ekranlardaki text/textbutton/label metinleri. Satır bazlı, betiği çalıştırmaz.
/// </summary>
public static partial class RenPyScriptParser
{
    private const string Dq = "\"(?<t>(?:[^\"\\\\]|\\\\.)*)\"";
    private const string Sq = "'(?<t>(?:[^'\\\\]|\\\\.)*)'";
    private const string Str = "(?:" + Dq + "|" + Sq + ")";
    private const string Tail = @"(?:\s*#.*)?$";

    [GeneratedRegex(@"^(?:(?<who>[A-Za-z_]\w*)(?:\s+[A-Za-z_@]\w*)*\s+)?" + Str + @"(?:\s+(?:nointeract|with\s+\S.*|\(.*\)))?\s*" + Tail)]
    private static partial Regex SayRegex();

    [GeneratedRegex("^" + Str + @"(?:\s*\(.*\))?(?:\s+if\s+.+)?\s*:\s*" + Tail)]
    private static partial Regex MenuChoiceRegex();

    [GeneratedRegex(@"(?<![\w.])_\(\s*" + Str + @"\s*\)")]
    private static partial Regex UnderscoreRegex();

    [GeneratedRegex(@"^(?:text|textbutton|label|tooltip)\s+" + Str + @"(?=\s|:|$)")]
    private static partial Regex ScreenTextRegex();

    [GeneratedRegex(@"^(?:(?:init\s+(?:-?\d+\s+)?)?python\b.*|(?:style|transform|image|translate)\b.*):\s*" + Tail)]
    private static partial Regex CodeBlockRegex();

    [GeneratedRegex(@"^screen\s+\w+.*:\s*" + Tail)]
    private static partial Regex ScreenBlockRegex();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex SpacesRegex();

    private static readonly HashSet<string> NotSpeakers = new(StringComparer.Ordinal)
    {
        "show", "scene", "hide", "play", "queue", "stop", "voice", "image", "define", "default", "jump", "call",
        "return", "if", "elif", "else", "while", "for", "label", "menu", "screen", "text", "textbutton", "style",
        "transform", "init", "python", "pause", "window", "with", "translate", "old", "new", "key", "add", "use",
        "has", "action", "renpy", "pass", "camera", "at", "as", "behind", "onlayer", "zorder", "expression", "import", "from"
    };

    private enum Block { None, Code, Screen }

    public static IEnumerable<TextUnit> Parse(string scriptText, string fileName)
    {
        var lines = scriptText.Split('\n');
        var block = Block.None;
        var blockIndent = 0;
        var inTriple = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i].TrimEnd('\r');
            if (i == 0) raw = raw.TrimStart('﻿');
            var line = raw.TrimStart();
            if (line.Length == 0) continue;
            var indent = raw.Length - line.Length;

            // Üç tırnaklı dizgeler (çok satırlı) atlanır.
            var triples = CountTriples(line);
            if (inTriple) { if (triples % 2 == 1) inTriple = false; continue; }
            if (triples > 0) { if (triples % 2 == 1) inTriple = true; continue; }

            if (line[0] == '#') continue;

            if (block != Block.None && indent <= blockIndent) block = Block.None;

            var id = $"{fileName}:{i + 1}";

            if (block == Block.None)
            {
                if (CodeBlockRegex().IsMatch(line)) { block = Block.Code; blockIndent = indent; continue; }
                if (ScreenBlockRegex().IsMatch(line)) { block = Block.Screen; blockIndent = indent; continue; }

                var choice = MenuChoiceRegex().Match(line);
                if (choice.Success)
                {
                    yield return new TextUnit(id, Unescape(choice.Groups["t"].Value), "menu");
                    continue;
                }

                var say = SayRegex().Match(line);
                if (say.Success && !(say.Groups["who"].Success && NotSpeakers.Contains(say.Groups["who"].Value)))
                {
                    yield return new TextUnit(id, Unescape(say.Groups["t"].Value), say.Groups["who"].Success ? say.Groups["who"].Value : null);
                    continue;
                }
            }
            else if (block == Block.Screen)
            {
                var st = ScreenTextRegex().Match(line);
                if (st.Success)
                {
                    yield return new TextUnit(id, Unescape(st.Groups["t"].Value), "screen");
                    continue;
                }
            }

            foreach (Match m in UnderscoreRegex().Matches(line))
                yield return new TextUnit(id, Unescape(m.Groups["t"].Value), "string");
        }
    }

    private static int CountTriples(string line)
    {
        var n = 0;
        for (var i = 0; i + 2 < line.Length; i++)
        {
            if ((line[i] == '"' || line[i] == '\'') && line[i + 1] == line[i] && line[i + 2] == line[i]) { n++; i += 2; }
        }
        return n;
    }

    /// <summary>Betikteki dizgeyi oyunun çalışma zamanında gördüğü metne çevirir.</summary>
    public static string Unescape(string literal)
    {
        literal = SpacesRegex().Replace(literal, " ");
        if (literal.IndexOf('\\') < 0) return literal;
        var sb = new StringBuilder(literal.Length);
        for (var i = 0; i < literal.Length; i++)
        {
            var c = literal[i];
            if (c != '\\' || i + 1 >= literal.Length) { sb.Append(c); continue; }
            var n = literal[++i];
            sb.Append(n == 'n' ? '\n' : n);
        }
        return sb.ToString();
    }
}
