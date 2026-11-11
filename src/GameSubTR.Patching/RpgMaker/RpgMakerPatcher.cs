using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GameSubTR.Core.Logging;

namespace GameSubTR.Patching.RpgMaker;

/// <summary>
/// RPG Maker MV/MZ yaması: data/*.json içindeki diyalog, seçenek, veritabanı adları/açıklamaları ve
/// sistem terimlerini yerinde çevirir (her dosya yedeklenir). Oyun başlığı çevrilmez: MV kayıt
/// dosyalarını başlıkla eşleştirir, değişirse eski kayıtlar görünmez olur.
/// </summary>
public sealed partial class RpgMakerPatcher : IGamePatcher
{
    public const string FontFileName = "gamesubtr-tr.ttf";
    private const int MinWrap = 40;

    /// <summary>Oyunun fontunu Türkçe karakter içeren bir fontla değiştirir.</summary>
    public bool ReplaceFont { get; init; } = true;
    public string FontSourcePath { get; init; } = @"C:\Windows\Fonts\arial.ttf";

    private static readonly string[] DatabaseFiles = { "Actors", "Classes", "Items", "Weapons", "Armors", "Skills", "States", "Enemies" };
    private static readonly string[] DatabaseFields = { "name", "nickname", "profile", "description", "message1", "message2", "message3", "message4" };
    private static readonly string[] SystemArrays = { "armorTypes", "weaponTypes", "skillTypes", "equipTypes", "elements" };
    private static readonly string[] TermArrays = { "basic", "commands", "params" };

    private static readonly JsonSerializerOptions WriteOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [GeneratedRegex(@"url\([^)]*\)")]
    private static partial Regex CssUrlRegex();

    public bool CanHandle(EngineKind engine) => engine is EngineKind.RpgMakerMV or EngineKind.RpgMakerMZ;

    /// <summary>Metni ziyaret eder; null dönerse metin değişmez.</summary>
    private delegate string? Visitor(string id, string source);

    public Task<IReadOnlyList<TextUnit>> ExtractAsync(GameInfo game, CancellationToken ct)
    {
        var units = new List<TextUnit>();
        foreach (var file in DataFiles(game))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);
            var root = Load(file, out _);
            if (root == null) continue;
            Walk(root, name, game.Engine, (id, src) => { units.Add(new TextUnit(id, src, name)); return null; });
        }
        return Task.FromResult<IReadOnlyList<TextUnit>>(units);
    }

    public Task InstallAsync(GameInfo game, IReadOnlyDictionary<string, string> translations, BackupManager backup, CancellationToken ct)
    {
        var dataRel = Path.GetRelativePath(game.RootDir, game.DataDir);
        var fontReady = ReplaceFont && File.Exists(FontSourcePath);
        if (ReplaceFont && !fontReady) Log.Warn("Yedek font bulunamadı, font değiştirilmeyecek: " + FontSourcePath);

        foreach (var file in DataFiles(game))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);
            var root = Load(file, out var bom);
            if (root == null) continue;

            var changed = Walk(root, name, game.Engine, (_, src) => translations.TryGetValue(src, out var t) && t != src ? t : null);

            if (fontReady && game.Engine == EngineKind.RpgMakerMZ && name.Equals("System.json", StringComparison.OrdinalIgnoreCase)
                && root is JsonObject sys)
            {
                if (sys["advanced"] is not JsonObject adv) sys["advanced"] = adv = new JsonObject();
                adv["mainFontFilename"] = FontFileName;
                changed = true;
            }

            if (!changed) continue;
            var bytes = new UTF8Encoding(false).GetBytes(root.ToJsonString(WriteOptions));
            if (bom) bytes = Encoding.UTF8.GetPreamble().Concat(bytes).ToArray();
            backup.WriteFile(Path.Combine(dataRel, "data", name), bytes);
        }

        if (fontReady)
        {
            backup.WriteFile(Path.Combine(dataRel, "fonts", FontFileName), File.ReadAllBytes(FontSourcePath));
            var css = Path.Combine(game.DataDir, "fonts", "gamefont.css");
            if (game.Engine == EngineKind.RpgMakerMV && File.Exists(css))
            {
                var text = File.ReadAllText(css);
                var replaced = CssUrlRegex().Replace(text, $"url(\"{FontFileName}\")");
                if (replaced != text)
                    backup.WriteFile(Path.Combine(dataRel, "fonts", "gamefont.css"), new UTF8Encoding(false).GetBytes(replaced));
            }
        }
        return Task.CompletedTask;
    }

    private static IEnumerable<string> DataFiles(GameInfo game)
    {
        var dir = Path.Combine(game.DataDir, "data");
        return Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            : Array.Empty<string>();
    }

    private static JsonNode? Load(string file, out bool bom)
    {
        var bytes = File.ReadAllBytes(file);
        bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        try { return JsonNode.Parse(bytes.AsSpan(bom ? 3 : 0)); }
        catch (JsonException ex)
        {
            Log.Warn($"JSON okunamadı, atlandı: {file} ({ex.Message})");
            return null;
        }
    }

    // ---- Gezinme (çıkarma ve kurulum aynı yolu kullanır) ----

    private static bool Walk(JsonNode root, string fileName, EngineKind engine, Visitor visit)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var changed = false;

        if (stem.StartsWith("Map", StringComparison.OrdinalIgnoreCase) && stem.Length > 3 && char.IsDigit(stem[3]) && root is JsonObject map)
        {
            changed |= VisitField(map, "displayName", $"{fileName}:displayName", visit);
            if (map["events"] is JsonArray events)
                for (var e = 0; e < events.Count; e++)
                    if (events[e]?["pages"] is JsonArray pages)
                        for (var p = 0; p < pages.Count; p++)
                            if (pages[p]?["list"] is JsonArray list)
                                changed |= WalkCommands(list, $"{fileName}:events[{e}].pages[{p}]", engine, visit);
        }
        else if (stem.Equals("CommonEvents", StringComparison.OrdinalIgnoreCase) && root is JsonArray ces)
        {
            for (var i = 0; i < ces.Count; i++)
                if (ces[i]?["list"] is JsonArray list)
                    changed |= WalkCommands(list, $"{fileName}:[{i}]", engine, visit);
        }
        else if (stem.Equals("Troops", StringComparison.OrdinalIgnoreCase) && root is JsonArray troops)
        {
            for (var i = 0; i < troops.Count; i++)
                if (troops[i]?["pages"] is JsonArray pages)
                    for (var p = 0; p < pages.Count; p++)
                        if (pages[p]?["list"] is JsonArray list)
                            changed |= WalkCommands(list, $"{fileName}:[{i}].pages[{p}]", engine, visit);
        }
        else if (DatabaseFiles.Contains(stem, StringComparer.OrdinalIgnoreCase) && root is JsonArray rows)
        {
            for (var i = 0; i < rows.Count; i++)
                if (rows[i] is JsonObject row)
                    foreach (var f in DatabaseFields)
                        changed |= VisitField(row, f, $"{fileName}:[{i}].{f}", visit);
        }
        else if (stem.Equals("System", StringComparison.OrdinalIgnoreCase) && root is JsonObject sys)
        {
            foreach (var a in SystemArrays)
                if (sys[a] is JsonArray arr) changed |= VisitArray(arr, $"{fileName}:{a}", visit);
            if (sys["terms"] is JsonObject terms)
            {
                foreach (var a in TermArrays)
                    if (terms[a] is JsonArray arr) changed |= VisitArray(arr, $"{fileName}:terms.{a}", visit);
                if (terms["messages"] is JsonObject msgs)
                    foreach (var key in msgs.Select(kv => kv.Key).ToList())
                        changed |= VisitField(msgs, key, $"{fileName}:terms.messages.{key}", visit);
            }
        }
        return changed;
    }

    private static string? AsText(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    private static bool VisitField(JsonObject obj, string field, string id, Visitor visit)
    {
        var src = AsText(obj[field]);
        if (src == null) return false;
        var t = visit(id, src);
        if (t == null) return false;
        obj[field] = t;
        return true;
    }

    private static bool VisitArray(JsonArray arr, string id, Visitor visit)
    {
        var changed = false;
        for (var i = 0; i < arr.Count; i++)
        {
            var src = AsText(arr[i]);
            if (src == null) continue;
            var t = visit($"{id}[{i}]", src);
            if (t == null) continue;
            arr[i] = t;
            changed = true;
        }
        return changed;
    }

    private static int Code(JsonNode? cmd) =>
        cmd?["code"] is JsonValue v && v.TryGetValue<int>(out var c) ? c : -1;

    private static bool WalkCommands(JsonArray list, string id, EngineKind engine, Visitor visit)
    {
        var changed = false;
        for (var i = 0; i < list.Count; i++)
        {
            var cmd = list[i];
            var code = Code(cmd);
            var pars = cmd?["parameters"] as JsonArray;
            if (pars == null) continue;

            if (code is 401 or 405)
            {
                // Ardışık metin satırları tek mesajdır.
                var start = i;
                var lines = new List<string>();
                while (i < list.Count && Code(list[i]) == code && list[i]?["parameters"] is JsonArray p)
                {
                    lines.Add(p.Count > 0 && p[0] is JsonValue lv && lv.TryGetValue<string>(out var ls) ? ls : "");
                    i++;
                }
                var count = i - start;
                i--;
                var joined = string.Join("\n", lines);
                if (joined.Trim().Length == 0) continue;
                var t = visit($"{id}.list[{start}]", joined);
                if (t == null) continue;

                var limit = Math.Max(MinWrap, lines.Max(l => PlaceholderGuard.VisibleLength(l, engine)));
                var wrapped = Wrap(t, limit, engine);
                var indent = list[start]?["indent"]?.DeepClone() ?? JsonValue.Create(0);
                for (var k = 0; k < count; k++) list.RemoveAt(start);
                for (var k = 0; k < wrapped.Count; k++)
                    list.Insert(start + k, new JsonObject
                    {
                        ["code"] = code,
                        ["indent"] = indent.DeepClone(),
                        ["parameters"] = new JsonArray(JsonValue.Create(wrapped[k]))
                    });
                i = start + wrapped.Count - 1;
                changed = true;
            }
            else if (code == 102 && pars.Count > 0 && pars[0] is JsonArray choices)
            {
                changed |= VisitArray(choices, $"{id}.list[{i}].choices", visit);
            }
            else if (code == 101 && pars.Count > 4)
            {
                var src = AsText(pars[4]);
                if (src == null) continue;
                var t = visit($"{id}.list[{i}].speaker", src);
                if (t == null) continue;
                pars[4] = t;
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>Metni sözcük sınırından, kontrol kodlarını saymadan <paramref name="limit"/> görünür karaktere sarar.</summary>
    public static List<string> Wrap(string text, int limit, EngineKind engine)
    {
        var result = new List<string>();
        foreach (var paragraph in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = new StringBuilder();
            var len = 0;
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var w = PlaceholderGuard.VisibleLength(word, engine);
                if (len > 0 && len + 1 + w > limit)
                {
                    result.Add(line.ToString());
                    line.Clear();
                    len = 0;
                }
                // Görünür uzunluğu sıfır olan kodlardan (\N[1]) sonra da boşluk korunur.
                if (line.Length > 0) { line.Append(' '); len++; }
                line.Append(word);
                len += w;
            }
            result.Add(line.ToString());
        }
        return result;
    }
}
