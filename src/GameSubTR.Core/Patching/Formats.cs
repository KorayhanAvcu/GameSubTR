using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace GameSubTR.Core.Patching;

public sealed record TextEntry(string Id, string Text);

/// <summary>
/// Bir dosya formatından metin çıkarır ve çevirileri aynı yerlere geri yazar.
/// Extract ve Apply aynı içerik üzerinde aynı Id'leri üretmelidir.
/// </summary>
public interface ITextFormat
{
    string Name { get; }
    bool CanHandle(string path);
    List<TextEntry> Extract(string content);
    string Apply(string content, IReadOnlyDictionary<string, string> translations);
}

public static class TextFormats
{
    public static readonly IReadOnlyList<ITextFormat> All = new ITextFormat[]
    {
        new JsonTextFormat(), new CsvTextFormat(), new XmlTextFormat(), new PoTextFormat(), new RenPyTextFormat(), new KeyValueTextFormat()
    };

    public static ITextFormat? For(string path) => All.FirstOrDefault(f => f.CanHandle(path));

    internal static string Ext(string path) => Path.GetExtension(path).ToLowerInvariant();
    internal static string Nl(string content) => content.Contains("\r\n") ? "\r\n" : "\n";
}

// ------------------------------------------------------------------ JSON

public sealed class JsonTextFormat : ITextFormat
{
    private static readonly HashSet<string> SkipKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "key", "guid", "uid", "icon", "image", "sprite", "texture", "sound", "audio", "sfx", "music", "path", "file",
        "type", "tag", "tags", "anim", "animation", "prefab", "scene", "class", "font", "color", "url", "event", "trigger", "ref", "asset"
    };

    private static readonly JsonDocumentOptions DocOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    public string Name => "JSON";
    public bool CanHandle(string path) => TextFormats.Ext(path) == ".json";

    public List<TextEntry> Extract(string content)
    {
        var list = new List<TextEntry>();
        var root = JsonNode.Parse(content, documentOptions: DocOptions);
        Walk(root, "$", null, (path, key, value) =>
        {
            if (key != null && SkipKeys.Contains(key)) return;
            if (TextFilter.IsTranslatable(value)) list.Add(new TextEntry(path, value));
        });
        return list;
    }

    public string Apply(string content, IReadOnlyDictionary<string, string> translations)
    {
        var root = JsonNode.Parse(content, documentOptions: DocOptions);
        var targets = new List<(JsonValue node, string path)>();
        Walk(root, "$", null, (path, key, value) => { }, (node, path) => targets.Add((node, path)));
        foreach (var (node, path) in targets)
        {
            if (!translations.TryGetValue(path, out var t)) continue;
            var parent = node.Parent;
            if (parent is JsonObject o) o[node.GetPropertyName()] = t;
            else if (parent is JsonArray a) a[node.GetElementIndex()] = t;
            else root = JsonValue.Create(t);
        }
        bool indented = Regex.IsMatch(content, @"\n[ \t]+[""\[{]");
        var opts = new JsonSerializerOptions { WriteIndented = indented, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var json = (root?.ToJsonString(opts) ?? "null").Replace("\r\n", "\n");
        return TextFormats.Nl(content) == "\r\n" ? json.Replace("\n", "\r\n") : json;
    }

    private static void Walk(JsonNode? node, string path, string? key, Action<string, string?, string> onString, Action<JsonValue, string>? onNode = null)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (k, v) in o.ToList()) Walk(v, path + "." + k, k, onString, onNode);
                break;
            case JsonArray a:
                for (int i = 0; i < a.Count; i++) Walk(a[i], $"{path}[{i}]", key, onString, onNode);
                break;
            case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                onString(path, key, v.GetValue<string>());
                onNode?.Invoke(v, path);
                break;
        }
    }
}

// ------------------------------------------------------------------ CSV / TSV

public sealed class CsvTextFormat : ITextFormat
{
    private static readonly string[] SourceNames = { "en", "english", "en-us", "en_us", "enus", "en-gb", "source", "text", "string", "value", "original" };
    private static readonly string[] TargetNames = { "tr", "turkish", "tr-tr", "tr_tr", "türkçe", "turkce" };

    public string Name => "CSV";
    public bool CanHandle(string path) => TextFormats.Ext(path) is ".csv" or ".tsv";

    private sealed class Cell { public string Value = ""; public bool Quoted; }

    private static char DetectDelimiter(string content)
    {
        var first = content.Split('\n')[0];
        int tabs = first.Count(c => c == '\t'), commas = first.Count(c => c == ','), semis = first.Count(c => c == ';');
        if (tabs >= commas && tabs >= semis && tabs > 0) return '\t';
        return semis > commas ? ';' : ',';
    }

    private static List<List<Cell>> Parse(string content, char d)
    {
        var rows = new List<List<Cell>>();
        var row = new List<Cell>();
        var cell = new Cell();
        var sb = new StringBuilder();
        bool inQ = false;
        for (int i = 0; i < content.Length; i++)
        {
            char c = content[i];
            if (inQ)
            {
                if (c == '"')
                {
                    if (i + 1 < content.Length && content[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQ = false;
                }
                else sb.Append(c);
                continue;
            }
            if (c == '"' && sb.Length == 0) { inQ = true; cell.Quoted = true; }
            else if (c == d) { cell.Value = sb.ToString(); row.Add(cell); cell = new Cell(); sb.Clear(); }
            else if (c == '\r') { }
            else if (c == '\n') { cell.Value = sb.ToString(); row.Add(cell); rows.Add(row); row = new List<Cell>(); cell = new Cell(); sb.Clear(); }
            else sb.Append(c);
        }
        if (sb.Length > 0 || cell.Quoted || row.Count > 0) { cell.Value = sb.ToString(); row.Add(cell); rows.Add(row); }
        return rows;
    }

    private static (int src, int dst) Columns(List<List<Cell>> rows)
    {
        if (rows.Count == 0) return (-1, -1);
        var header = rows[0].Select(c => c.Value.Trim().ToLowerInvariant()).ToList();
        int src = -1, dst = -1;
        foreach (var n in SourceNames) { src = header.IndexOf(n); if (src >= 0) break; }
        foreach (var n in TargetNames) { dst = header.IndexOf(n); if (dst >= 0) break; }
        if (src >= 0 && dst < 0) dst = src;
        return (src, dst);
    }

    public List<TextEntry> Extract(string content)
    {
        var rows = Parse(content, DetectDelimiter(content));
        var (src, _) = Columns(rows);
        var list = new List<TextEntry>();
        for (int r = 0; r < rows.Count; r++)
        {
            if (src >= 0)
            {
                if (r == 0 || src >= rows[r].Count) continue;
                var v = rows[r][src].Value;
                if (TextFilter.IsTranslatable(v)) list.Add(new TextEntry($"{r}:{src}", v));
            }
            else
            {
                for (int c = 0; c < rows[r].Count; c++)
                    if (TextFilter.IsTranslatable(rows[r][c].Value)) list.Add(new TextEntry($"{r}:{c}", rows[r][c].Value));
            }
        }
        return list;
    }

    public string Apply(string content, IReadOnlyDictionary<string, string> translations)
    {
        char d = DetectDelimiter(content);
        var rows = Parse(content, d);
        var (src, dst) = Columns(rows);
        foreach (var (id, text) in translations)
        {
            var parts = id.Split(':');
            int r = int.Parse(parts[0]), c = int.Parse(parts[1]);
            if (src >= 0) c = dst;
            if (r >= rows.Count) continue;
            while (rows[r].Count <= c) rows[r].Add(new Cell());
            rows[r][c].Value = text;
        }
        var nl = TextFormats.Nl(content);
        var sb = new StringBuilder();
        for (int r = 0; r < rows.Count; r++)
        {
            for (int c = 0; c < rows[r].Count; c++)
            {
                if (c > 0) sb.Append(d);
                var v = rows[r][c].Value;
                bool q = rows[r][c].Quoted || v.IndexOfAny(new[] { d, '"', '\n', '\r' }) >= 0;
                sb.Append(q ? "\"" + v.Replace("\"", "\"\"") + "\"" : v);
            }
            if (r < rows.Count - 1 || content.EndsWith('\n')) sb.Append(nl);
        }
        return sb.ToString();
    }
}

// ------------------------------------------------------------------ XML (.xml, .resx)

public sealed class XmlTextFormat : ITextFormat
{
    private static readonly HashSet<string> TextAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text", "value", "label", "title", "caption", "desc", "description", "tooltip", "string", "message", "hint", "content", "name_en", "en"
    };

    public string Name => "XML";
    public bool CanHandle(string path) => TextFormats.Ext(path) is ".xml" or ".resx";

    private static IEnumerable<(string id, Func<string> get, Action<string> set)> Targets(XDocument doc)
    {
        int t = 0, a = 0;
        foreach (var node in doc.DescendantNodes())
        {
            if (node is XText text && node.Parent != null)
            {
                int id = t++;
                yield return ($"t{id}", () => text.Value, v => text.Value = v);
            }
            else if (node is XElement el)
            {
                foreach (var attr in el.Attributes())
                {
                    int id = a++;
                    if (TextAttributes.Contains(attr.Name.LocalName))
                        yield return ($"a{id}", () => attr.Value, v => attr.Value = v);
                }
            }
        }
    }

    public List<TextEntry> Extract(string content)
    {
        var doc = XDocument.Parse(content, LoadOptions.PreserveWhitespace);
        return Targets(doc).Select(x => new TextEntry(x.id, x.get())).Where(e => TextFilter.IsTranslatable(e.Text)).ToList();
    }

    public string Apply(string content, IReadOnlyDictionary<string, string> translations)
    {
        var doc = XDocument.Parse(content, LoadOptions.PreserveWhitespace);
        foreach (var (id, _, set) in Targets(doc).ToList())
            if (translations.TryGetValue(id, out var v)) set(v);
        var body = doc.ToString(SaveOptions.DisableFormatting).Replace("\r\n", "\n");
        if (TextFormats.Nl(content) == "\r\n") body = body.Replace("\n", "\r\n");
        if (doc.Declaration == null) return body;
        // Bildirimden sonraki satır sonunu işletim sistemine değil orijinal dosyaya göre yaz.
        var after = Regex.Match(content, @"^\uFEFF?\s*<\?xml[^>]*\?>(?<nl>\r?\n)?");
        return doc.Declaration + (after.Success ? after.Groups["nl"].Value : "") + body.TrimStart('\r', '\n');
    }
}

// ------------------------------------------------------------------ Gettext PO

public sealed class PoTextFormat : ITextFormat
{
    public string Name => "PO";
    public bool CanHandle(string path) => TextFormats.Ext(path) is ".po" or ".pot";

    private sealed record Entry(int MsgIdLine, string MsgId, int StrStart, int StrEnd);

    private static string Unq(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') s = s[1..^1];
        return s.Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\\"", "\"").Replace("\\\\", "\\");
    }

    private static string Q(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";

    private static List<Entry> Scan(string[] lines)
    {
        var list = new List<Entry>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("msgid ")) continue;
            int idLine = i;
            var sb = new StringBuilder(Unq(lines[i][6..]));
            int j = i + 1;
            while (j < lines.Length && lines[j].TrimStart().StartsWith('"')) sb.Append(Unq(lines[j++]));
            if (j < lines.Length && lines[j].StartsWith("msgid_plural")) { i = j; continue; } // çoğul girişleri atla
            if (j >= lines.Length || !lines[j].StartsWith("msgstr ")) { i = j - 1; continue; }
            int strStart = j++;
            while (j < lines.Length && lines[j].TrimStart().StartsWith('"')) j++;
            list.Add(new Entry(idLine, sb.ToString(), strStart, j - 1));
            i = j - 1;
        }
        return list;
    }

    public List<TextEntry> Extract(string content)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        return Scan(lines).Where(e => TextFilter.IsTranslatable(e.MsgId)).Select(e => new TextEntry($"L{e.MsgIdLine}", e.MsgId)).ToList();
    }

    public string Apply(string content, IReadOnlyDictionary<string, string> translations)
    {
        var nl = TextFormats.Nl(content);
        var lines = content.Replace("\r\n", "\n").Split('\n').ToList();
        foreach (var e in Scan(lines.ToArray()).OrderByDescending(e => e.StrStart))
        {
            if (!translations.TryGetValue($"L{e.MsgIdLine}", out var t)) continue;
            lines.RemoveRange(e.StrStart, e.StrEnd - e.StrStart + 1);
            lines.Insert(e.StrStart, "msgstr " + Q(t));
        }
        return string.Join(nl, lines);
    }
}

// ------------------------------------------------------------------ Ren'Py (.rpy)

public sealed partial class RenPyTextFormat : ITextFormat
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "play", "queue", "stop", "show", "scene", "hide", "image", "define", "default", "jump", "call", "label", "init",
        "voice", "sound", "window", "style", "transform", "screen", "font", "python", "return", "pause", "with", "at", "old", "translate", "use", "add"
    };

    // [girinti][konuşmacı ve özellikler]"metin"[kuyruk]   (kuyruk: with x, ':' (menü), if ...)
    [GeneratedRegex(@"^(?<pre>\s*(?<who>(?:[A-Za-z_][\w.]*\s+)*))""(?<text>(?:[^""\\]|\\.)*)""(?<post>\s*(?:with\s+\w+|if\s+.+?)?\s*:?\s*(?:#.*)?)$")]
    private static partial Regex Line();

    public string Name => "Ren'Py";
    public bool CanHandle(string path) => TextFormats.Ext(path) == ".rpy";

    private static string Unesc(string s) => s.Replace("\\\"", "\"").Replace("\\n", "\n");
    private static string Esc(string s) => s.Replace("\"", "\\\"").Replace("\n", "\\n");

    private static bool Accept(Match m)
    {
        var who = m.Groups["who"].Value.Trim();
        var first = who.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first == null || first == "new" || !Keywords.Contains(first);
    }

    public List<TextEntry> Extract(string content)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        var list = new List<TextEntry>();
        for (int i = 0; i < lines.Length; i++)
        {
            var m = Line().Match(lines[i]);
            if (!m.Success || !Accept(m)) continue;
            var text = Unesc(m.Groups["text"].Value);
            if (TextFilter.IsTranslatable(text)) list.Add(new TextEntry($"L{i}", text));
        }
        return list;
    }

    public string Apply(string content, IReadOnlyDictionary<string, string> translations)
    {
        var nl = TextFormats.Nl(content);
        var lines = content.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (!translations.TryGetValue($"L{i}", out var t)) continue;
            var m = Line().Match(lines[i]);
            if (!m.Success) continue;
            lines[i] = m.Groups["pre"].Value + "\"" + Esc(t) + "\"" + m.Groups["post"].Value;
        }
        return string.Join(nl, lines);
    }
}

// ------------------------------------------------------------------ INI / properties / key=value / düz satır

public sealed partial class KeyValueTextFormat : ITextFormat
{
    [GeneratedRegex(@"^(?<pre>\s*[^\s=#;/\[][^=]*?\s*=\s*)(?<q>""?)(?<val>.*?)\k<q>(?<post>\s*)$")]
    private static partial Regex Kv();

    public string Name => "INI/TXT";
    public bool CanHandle(string path) => TextFormats.Ext(path) is ".ini" or ".properties" or ".cfg" or ".lang" or ".txt" or ".strings" or ".loc" or ".locale";

    private static bool IsComment(string l)
    {
        var t = l.TrimStart();
        return t.Length == 0 || t[0] == '#' || t[0] == ';' || t.StartsWith("//") || (t[0] == '[' && t.TrimEnd().EndsWith(']'));
    }

    /// <summary>Dosyanın çoğu "anahtar = değer" ise KV modu, değilse her satır ayrı metin.</summary>
    private static bool IsKvMode(string[] lines)
    {
        int total = 0, kv = 0;
        foreach (var l in lines)
        {
            if (IsComment(l)) continue;
            total++;
            if (Kv().IsMatch(l)) kv++;
        }
        return total == 0 || kv >= total * 0.6;
    }

    public List<TextEntry> Extract(string content)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        bool kvMode = IsKvMode(lines);
        var list = new List<TextEntry>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (IsComment(lines[i])) continue;
            string val;
            if (kvMode)
            {
                var m = Kv().Match(lines[i]);
                if (!m.Success) continue;
                val = m.Groups["val"].Value;
            }
            else val = lines[i].Trim();
            if (TextFilter.IsTranslatable(val)) list.Add(new TextEntry($"L{i}", val));
        }
        return list;
    }

    public string Apply(string content, IReadOnlyDictionary<string, string> translations)
    {
        var nl = TextFormats.Nl(content);
        var lines = content.Replace("\r\n", "\n").Split('\n');
        bool kvMode = IsKvMode(lines);
        for (int i = 0; i < lines.Length; i++)
        {
            if (!translations.TryGetValue($"L{i}", out var t)) continue;
            t = t.Replace("\n", "\\n");
            if (kvMode)
            {
                var m = Kv().Match(lines[i]);
                if (m.Success) lines[i] = m.Groups["pre"].Value + m.Groups["q"].Value + t + m.Groups["q"].Value + m.Groups["post"].Value;
            }
            else
            {
                var lead = lines[i][..(lines[i].Length - lines[i].TrimStart().Length)];
                lines[i] = lead + t;
            }
        }
        return string.Join(nl, lines);
    }
}
