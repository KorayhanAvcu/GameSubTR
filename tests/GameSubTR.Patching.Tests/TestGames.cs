using System.Security.Cryptography;
using System.Text;

namespace GameSubTR.Patching.Tests;

/// <summary>Geçici klasörde sahte oyun dizinleri üretir.</summary>
internal static class TestGames
{
    public static string NewDir(string name = "Game")
    {
        var dir = Path.Combine(Path.GetTempPath(), "gstr-patch-" + Guid.NewGuid().ToString("N"), name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void Write(string root, string rel, string text, bool bom = false) =>
        WriteBytes(root, rel, (bom ? Encoding.UTF8.GetPreamble() : Array.Empty<byte>()).Concat(new UTF8Encoding(false).GetBytes(text)).ToArray());

    public static void WriteBytes(string root, string rel, byte[] bytes)
    {
        var p = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, bytes);
    }

    public static string Read(string root, string rel) =>
        File.ReadAllText(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)), Encoding.UTF8);

    public const string RenPyScript = """
        label start:
            e "Hello, [player]!"
            "It was a dark night."
            e happy "Nice \"day\"." with dissolve
            menu:
                "Go left" if seen:
                    jump left
            show eileen happy
            $ x = "not dialogue"
            textbutton _("Start") action Start()

        """;

    public static string CreateRenPy(Action<string>? fill = null)
    {
        var root = NewDir("RenPyGame");
        Write(root, "renpy/__init__.py", "# renpy");
        Write(root, "game/script.rpy", RenPyScript);
        fill?.Invoke(Path.Combine(root, "game"));
        return root;
    }

    public const string Map001 = """
        {"displayName":"","events":[null,{"id":1,"name":"EV001","pages":[{"list":[
        {"code":101,"indent":0,"parameters":["Actor1",0,0,2]},
        {"code":401,"indent":0,"parameters":["Hello \\N[1]."]},
        {"code":401,"indent":0,"parameters":["How are you?"]},
        {"code":102,"indent":0,"parameters":[["Yes","No"],1,0,2,0]},
        {"code":402,"indent":0,"parameters":[0,"Yes"]},
        {"code":201,"indent":1,"parameters":[0,2,5,5,0,0]},
        {"code":0,"indent":1,"parameters":[]},
        {"code":0,"indent":0,"parameters":[]}]}]}]}
        """;

    public const string Items = """
        [null,{"id":1,"name":"Potion","description":"Restores some HP.","note":""},{"id":2,"name":"","description":"","note":""}]
        """;

    public const string SystemMv = """
        {"gameTitle":"Test Quest","armorTypes":["","Shield"],"elements":["","Fire"],"equipTypes":["","Weapon"],"skillTypes":["","Magic"],"weaponTypes":["","Sword"],
        "terms":{"basic":["Level","Lv"],"commands":["Fight","Escape",null],"params":["Max HP"],"messages":{"actionFailure":"There was no effect on %1!"}}}
        """;

    public static string CreateRpgMv(bool www = true)
    {
        var root = NewDir("MvGame");
        var d = www ? "www/" : "";
        Write(root, d + "js/rpg_core.js", "// core");
        Write(root, d + "data/System.json", SystemMv);
        Write(root, d + "data/Map001.json", Map001);
        Write(root, d + "data/Items.json", Items);
        Write(root, d + "data/CommonEvents.json", "[null]");
        Write(root, d + "fonts/gamefont.css", "@font-face {\n    font-family: GameFont;\n    src: url(\"mplus-1m-regular.ttf\");\n}\n");
        return root;
    }

    public static string CreateRpgMz()
    {
        var root = NewDir("MzGame");
        Write(root, "js/rmmz_core.js", "// core");
        Write(root, "data/System.json", SystemMv.TrimEnd().TrimEnd('}') + ",\"advanced\":{\"mainFontFilename\":\"mplus-1m-regular.woff\",\"fontSize\":26}}");
        Write(root, "data/Map001.json", Map001.Replace("[\"Actor1\",0,0,2]", "[\"Actor1\",0,0,2,\"Old Man\"]"));
        Write(root, "data/Items.json", Items);
        return root;
    }

    public static string CreateEmpty() => NewDir("Empty");

    /// <summary>Klasördeki tüm dosyaların göreli yol → SHA256 haritası.</summary>
    public static SortedDictionary<string, string> Snapshot(string root)
    {
        var d = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            d[Path.GetRelativePath(root, f).Replace('\\', '/')] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)));
        return d;
    }
}
