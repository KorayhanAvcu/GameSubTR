using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace GameSubTR.Patching.RenPy;

/// <summary>
/// Ren'Py yaması: orijinal dosyalara dokunmaz, iki dosya ekler. Sözlük (gamesubtr_tr.json)
/// küçük bir betikle (zz_gamesubtr_tr.rpy) diyalog/menü metnine ve arayüz dizgelerine uygulanır.
/// </summary>
public sealed class RenPyPatcher : IGamePatcher
{
    public const string ScriptName = "zz_gamesubtr_tr.rpy";
    public const string MapName = "gamesubtr_tr.json";

    /// <summary>Arayüz fontlarını Ren'Py ile gelen (Türkçe karakterli) DejaVuSans yapar.</summary>
    public bool ReplaceFonts { get; init; } = true;

    public bool CanHandle(EngineKind engine) => engine == EngineKind.RenPy;

    public Task<IReadOnlyList<TextUnit>> ExtractAsync(GameInfo game, CancellationToken ct)
    {
        var units = new List<TextUnit>();
        var tl = Path.Combine(game.DataDir, "tl") + Path.DirectorySeparatorChar;
        foreach (var file in Directory.GetFiles(game.DataDir, "*.rpy", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (file.StartsWith(tl, StringComparison.OrdinalIgnoreCase)) continue;
            if (Path.GetFileName(file).Equals(ScriptName, StringComparison.OrdinalIgnoreCase)) continue;
            var rel = Path.GetRelativePath(game.DataDir, file).Replace('\\', '/');
            units.AddRange(RenPyScriptParser.Parse(File.ReadAllText(file, Encoding.UTF8), rel));
        }
        return Task.FromResult<IReadOnlyList<TextUnit>>(units);
    }

    public Task InstallAsync(GameInfo game, IReadOnlyDictionary<string, string> translations, BackupManager backup, CancellationToken ct)
    {
        var map = translations.Where(kv => kv.Key != kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value);
        var json = JsonSerializer.Serialize(map, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true });
        var utf8 = new UTF8Encoding(false);
        var dataRel = Path.GetRelativePath(game.RootDir, game.DataDir);
        backup.WriteFile(Path.Combine(dataRel, MapName), utf8.GetBytes(json));
        ct.ThrowIfCancellationRequested();
        backup.WriteFile(Path.Combine(dataRel, ScriptName), utf8.GetBytes(BuildScript(ReplaceFonts)));
        return Task.CompletedTask;
    }

    private static readonly string[] FontVars =
        { "gui.text_font", "gui.name_text_font", "gui.interface_text_font", "gui.button_text_font", "gui.choice_button_text_font" };

    public static string BuildScript(bool replaceFonts)
    {
        var fonts = replaceFonts ? string.Join("\n", FontVars.Select(v => $"define {v} = \"DejaVuSans.ttf\"")) + "\n" : "";
        return ScriptTemplate.Replace("__FONTS__\n", fonts).Replace("__MAP__", MapName);
    }

    private const string ScriptTemplate =
        "# GameSubTR.Patcher tarafından üretildi. Kaldırmak için Patcher'da \"Yamayı kaldır\".\n" +
        "init offset = -1\n" +
        "__FONTS__\n" +
        "init 999 python:\n" +
        "    import json as _gstr_json\n" +
        "    _gstr_map = {}\n" +
        "    try:\n" +
        "        _gstr_f = renpy.file(\"__MAP__\")\n" +
        "        _gstr_map = _gstr_json.loads(_gstr_f.read().decode(\"utf-8\"))\n" +
        "        _gstr_f.close()\n" +
        "    except Exception as _gstr_e:\n" +
        "        renpy.log(\"GameSubTR: %r\" % (_gstr_e,))\n" +
        "    _gstr_prev_filter = config.say_menu_text_filter\n" +
        "    def _gstr_filter(s):\n" +
        "        if _gstr_prev_filter is not None:\n" +
        "            s = _gstr_prev_filter(s)\n" +
        "        return _gstr_map.get(s, s)\n" +
        "    config.say_menu_text_filter = _gstr_filter\n" +
        "    _gstr_prev_replace = config.replace_text\n" +
        "    def _gstr_replace(s):\n" +
        "        if _gstr_prev_replace is not None:\n" +
        "            s = _gstr_prev_replace(s)\n" +
        "        return _gstr_map.get(s, s)\n" +
        "    config.replace_text = _gstr_replace\n";
}
