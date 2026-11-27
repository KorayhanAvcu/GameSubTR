using System.Text;
using GameSubTR.Core.Patching;
using Xunit;

namespace GameSubTR.Core.Tests;

public class PatchTests
{
    private static Dictionary<string, string> Tr(List<TextEntry> entries, Func<string, string> f) =>
        entries.ToDictionary(e => e.Id, e => f(e.Text));

    [Fact]
    public void Filter_SeparatesTextFromTechnicalValues()
    {
        foreach (var ok in new[] { "Inventory", "Press E to open", "You don't have enough gold.", "OK", "New Game" })
            Assert.True(TextFilter.IsTranslatable(ok));
        foreach (var no in new[] { "player_idle", "playerName", "MAX_HP", "true", "#FF00AA", "ui/icons/sword.png", "https://x.com", "42", "idle", "Sprites.Hero01", "ŞİMDİ ÇIK BURADAN" })
            Assert.False(TextFilter.IsTranslatable(no));
    }

    [Fact]
    public void Placeholders_RepairAndValidate()
    {
        Assert.True(Placeholders.Preserved("You have {0} gold", "{0} altının var"));
        Assert.False(Placeholders.Preserved("You have {0} gold", "Altının var"));
        var fixedText = Placeholders.Repair("Hello <b>{name}</b>!", "Merhaba < b > { name } </ b >!");
        Assert.Equal("Merhaba <b>{name}</b>!", fixedText);
        Assert.True(Placeholders.Preserved("Hello <b>{name}</b>!", fixedText));
        Assert.Equal("%s öldü", Placeholders.Repair("%s died", "% s öldü"));
    }

    [Fact]
    public void Json_RoundTrip_KeepsIdsAndUnicode()
    {
        var json = "{\n  \"id\": \"quest_01\",\n  \"title\": \"The Lost Sword\",\n  \"lines\": [\"Find the sword.\", \"Return to {npc}.\"],\n  \"icon\": \"Sword Icon\",\n  \"reward\": 50\n}";
        var f = new JsonTextFormat();
        var e = f.Extract(json);
        Assert.Equal(new[] { "The Lost Sword", "Find the sword.", "Return to {npc}." }, e.Select(x => x.Text).ToArray());
        var outJson = f.Apply(json, new Dictionary<string, string> { [e[0].Id] = "Kayıp Kılıç", [e[2].Id] = "{npc} yanına dön." });
        Assert.Contains("\"title\": \"Kayıp Kılıç\"", outJson);
        Assert.Contains("\"{npc} yanına dön.\"", outJson);
        Assert.Contains("\"id\": \"quest_01\"", outJson);
        Assert.Contains("\"reward\": 50", outJson);
    }

    [Fact]
    public void Csv_UsesEnglishColumn_WritesTurkishColumn()
    {
        var csv = "key,en,tr\nmenu_start,Start Game,\nmenu_quit,\"Quit, now\",\n";
        var f = new CsvTextFormat();
        var e = f.Extract(csv);
        Assert.Equal(new[] { "Start Game", "Quit, now" }, e.Select(x => x.Text).ToArray());
        var outCsv = f.Apply(csv, Tr(e, t => t == "Start Game" ? "Oyunu Başlat" : "Çık, şimdi"));
        Assert.Equal("key,en,tr\nmenu_start,Start Game,Oyunu Başlat\nmenu_quit,\"Quit, now\",\"Çık, şimdi\"\n", outCsv);
    }

    [Fact]
    public void Csv_WithoutTurkishColumn_ReplacesEnglish()
    {
        var csv = "Key\tEnglish\r\nA\tHello there\r\n";
        var f = new CsvTextFormat();
        var e = f.Extract(csv);
        Assert.Single(e);
        Assert.Equal("Key\tEnglish\r\nA\tMerhaba\r\n", f.Apply(csv, Tr(e, _ => "Merhaba")));
    }

    [Fact]
    public void Xml_TextAndAttributes()
    {
        var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<strings>\n  <s id=\"a\">Open the gate</s>\n  <item name=\"sword_01\" label=\"Iron Sword\"/>\n</strings>";
        var f = new XmlTextFormat();
        var e = f.Extract(xml);
        Assert.Equal(new[] { "Open the gate", "Iron Sword" }, e.Select(x => x.Text).ToArray());
        var outXml = f.Apply(xml, Tr(e, t => t == "Open the gate" ? "Kapıyı aç" : "Demir Kılıç"));
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<strings>", outXml);
        Assert.Contains("<s id=\"a\">Kapıyı aç</s>", outXml);
        Assert.Contains("name=\"sword_01\" label=\"Demir Kılıç\"", outXml);
    }

    [Fact]
    public void Po_FillsMsgstr()
    {
        var po = "msgid \"\"\nmsgstr \"\"\n\"Language: en\\n\"\n\n#: menu\nmsgid \"Load Game\"\nmsgstr \"\"\n";
        var f = new PoTextFormat();
        var e = f.Extract(po);
        Assert.Single(e);
        var outPo = f.Apply(po, Tr(e, _ => "Oyun Yükle"));
        Assert.Contains("msgid \"Load Game\"\nmsgstr \"Oyun Yükle\"", outPo);
        Assert.Contains("\"Language: en\\n\"", outPo);
    }

    [Fact]
    public void RenPy_DialogueAndMenus_NotCommands()
    {
        var rpy = "label start:\n    scene bg room\n    play music \"audio/theme.ogg\"\n    e \"Hello, \\\"friend\\\"!\"\n    \"It was a dark night.\"\n    menu:\n        \"Go left\":\n            jump left\n    define e = Character(\"Eileen\")\n";
        var f = new RenPyTextFormat();
        var e = f.Extract(rpy);
        Assert.Equal(new[] { "Hello, \"friend\"!", "It was a dark night.", "Go left" }, e.Select(x => x.Text).ToArray());
        var outRpy = f.Apply(rpy, Tr(e, t => t switch { "Go left" => "Sola git", "It was a dark night." => "Karanlık bir geceydi.", _ => "Merhaba \"dostum\"!" }));
        Assert.Contains("    e \"Merhaba \\\"dostum\\\"!\"\n", outRpy);
        Assert.Contains("        \"Sola git\":\n", outRpy);
        Assert.Contains("play music \"audio/theme.ogg\"", outRpy);
    }

    [Fact]
    public void KeyValue_And_PlainLines()
    {
        var ini = "[UI]\n; comment\nTitle=Main Menu\nHint = \"Press any key\"\nMaxFps=60\n";
        var f = new KeyValueTextFormat();
        var e = f.Extract(ini);
        Assert.Equal(new[] { "Main Menu", "Press any key" }, e.Select(x => x.Text).ToArray());
        Assert.Equal("[UI]\n; comment\nTitle=Ana Menü\nHint = \"Bir tuşa bas\"\nMaxFps=60\n",
            f.Apply(ini, Tr(e, t => t == "Main Menu" ? "Ana Menü" : "Bir tuşa bas")));

        var txt = "Welcome to the village.\nThe shop is closed.\n";
        var e2 = f.Extract(txt);
        Assert.Equal(2, e2.Count);
    }

    [Fact]
    public void Project_ExportImportRestore_EndToEnd()
    {
        var game = Path.Combine(Path.GetTempPath(), "gstr-game-" + Guid.NewGuid().ToString("N"));
        var outDir = Path.Combine(game + "-out");
        Directory.CreateDirectory(Path.Combine(game, "Data"));
        var jsonPath = Path.Combine(game, "Data", "dialog.json");
        var csvPath = Path.Combine(game, "Data", "ui.csv");
        File.WriteAllText(jsonPath, "{\"a\":\"Hello there\",\"b\":\"You have {0} gold\",\"c\":\"Line one\\nLine two\"}", new UTF8Encoding(true));
        File.WriteAllText(csvPath, "key,en\nhello,Hello there\n");
        File.WriteAllBytes(Path.Combine(game, "Data", "level.bin"), new byte[] { 0, 1, 2, 3 });

        var (project, summary) = PatchProject.Export(game, outDir);
        Assert.Equal(2, summary.Files);
        Assert.Equal(3, summary.Lines); // "Hello there" iki dosyada ama tek satır
        var src = File.ReadAllLines(Path.Combine(outDir, PatchProject.SourceFileName));
        Assert.Equal(new[] { "Hello there", "You have {0} gold", "Line one ¶ Line two" }, src);
        Assert.True(File.Exists(Path.Combine(outDir, "parca_001.txt")));

        // Google Translate'in tipik çıktısı: kod parçasına boşluk eklemiş.
        var trFile = Path.Combine(outDir, "metinler_tr.txt");
        File.WriteAllText(trFile, "Merhaba\n{ 0 } altının var\nBirinci satır ¶ İkinci satır\n");
        var loaded = PatchProject.Load(Path.Combine(outDir, PatchProject.ProjectFileName));
        var report = loaded.Apply(PatchProject.ReadTranslation(new[] { trFile }));
        Assert.Equal(3, report.Applied);
        Assert.Equal(2, report.FilesWritten);

        var patchedJsonBytes = File.ReadAllBytes(jsonPath);
        Assert.Equal(0xEF, patchedJsonBytes[0]); // BOM korundu
        var patchedJson = File.ReadAllText(jsonPath);
        Assert.Contains("\"a\":\"Merhaba\"", patchedJson);
        Assert.Contains("\"b\":\"{0} altının var\"", patchedJson);
        Assert.Contains("\"c\":\"Birinci satır\\nİkinci satır\"", patchedJson);
        Assert.Contains("hello,Merhaba", File.ReadAllText(csvPath));

        // Tekrar içe aktarma yedekten çalışır (çift çeviri olmaz).
        loaded.Apply(PatchProject.ReadTranslation(new[] { trFile }));
        Assert.Contains("\"a\":\"Merhaba\"", File.ReadAllText(jsonPath));

        Assert.Equal(2, PatchProject.Restore(game));
        Assert.Contains("\"a\":\"Hello there\"", File.ReadAllText(jsonPath));
        Assert.False(Directory.Exists(Path.Combine(game, PatchProject.BackupFolderName)));
    }

    [Fact]
    public void Project_LineCountMismatch_Throws()
    {
        var p = new PatchProject { Lines = { new PatchLine { Text = "A b" }, new PatchLine { Text = "C d" } } };
        Assert.Throws<InvalidDataException>(() => p.Apply(new[] { "only one" }));
    }
}
