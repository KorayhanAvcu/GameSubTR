using System.Text.Json.Nodes;
using GameSubTR.Patching.RpgMaker;
using Xunit;

namespace GameSubTR.Patching.Tests;

public class RpgMakerTests
{
    private static string FakeFont()
    {
        var p = Path.Combine(TestGames.NewDir("font"), "fake.ttf");
        File.WriteAllBytes(p, new byte[] { 1, 2, 3, 4 });
        return p;
    }

    private static RpgMakerPatcher NoFont() => new() { ReplaceFont = false };

    private static async Task<List<TextUnit>> Extract(string root) =>
        (await NoFont().ExtractAsync(EngineDetector.Detect(root), default)).ToList();

    private static async Task<BackupManager> Install(string root, RpgMakerPatcher patcher, Dictionary<string, string> tr)
    {
        var backup = new BackupManager(root);
        await patcher.InstallAsync(EngineDetector.Detect(root), tr, backup, default);
        backup.Commit();
        return backup;
    }

    private static JsonArray CommandList(string root, string dataPrefix)
    {
        var map = JsonNode.Parse(TestGames.Read(root, dataPrefix + "data/Map001.json"))!;
        return map["events"]![1]!["pages"]![0]!["list"]!.AsArray();
    }

    [Fact]
    public async Task Extract_JoinsConsecutive401()
    {
        var units = await Extract(TestGames.CreateRpgMv());
        Assert.Contains(units, u => u.Source == "Hello \\N[1].\nHow are you?" && u.Context == "Map001.json");
        Assert.DoesNotContain(units, u => u.Source == "How are you?");
    }

    [Fact]
    public async Task Extract_Choices_Database_SystemTerms()
    {
        var s = (await Extract(TestGames.CreateRpgMv())).Select(u => u.Source).ToList();
        foreach (var expected in new[] { "Yes", "No", "Potion", "Restores some HP.", "Fight", "Escape", "Level", "Max HP", "Shield", "Fire", "There was no effect on %1!" })
            Assert.Contains(expected, s);
        Assert.DoesNotContain("", s);
        Assert.DoesNotContain("Test Quest", s); // başlık çevrilmez (kayıt dosyaları başlığa bağlı)
        Assert.DoesNotContain("Actor1", s);     // yüz grafiği dosya adı
    }

    [Fact]
    public async Task Extract_Mz_SpeakerName()
    {
        var units = await Extract(TestGames.CreateRpgMz());
        Assert.Contains(units, u => u.Source == "Old Man" && u.Id.EndsWith(".speaker"));
    }

    [Fact]
    public async Task Install_ReplacesTextAndKeepsStructure()
    {
        var root = TestGames.CreateRpgMv();
        await Install(root, NoFont(), new Dictionary<string, string>
        {
            ["Hello \\N[1].\nHow are you?"] = "Merhaba \\N[1].\nNasılsın?",
            ["Yes"] = "Evet",
            ["Potion"] = "İksir",
            ["Fight"] = "Savaş"
        });

        var list = CommandList(root, "www/");
        Assert.Equal(new[] { 101, 401, 401, 102, 402, 201, 0, 0 }, list.Select(c => c!["code"]!.GetValue<int>()));
        Assert.Equal("Merhaba \\N[1].", list[1]!["parameters"]![0]!.GetValue<string>());
        Assert.Equal("Nasılsın?", list[2]!["parameters"]![0]!.GetValue<string>());
        Assert.Equal("Evet", list[3]!["parameters"]![0]![0]!.GetValue<string>());
        Assert.Equal("No", list[3]!["parameters"]![0]![1]!.GetValue<string>());
        Assert.Equal("[0,2,5,5,0,0]", list[5]!["parameters"]!.ToJsonString());
        Assert.Equal(1, list[5]!["indent"]!.GetValue<int>());

        var items = JsonNode.Parse(TestGames.Read(root, "www/data/Items.json"))!;
        Assert.Equal("İksir", items[1]!["name"]!.GetValue<string>());
        Assert.Equal("Restores some HP.", items[1]!["description"]!.GetValue<string>());
        var sys = JsonNode.Parse(TestGames.Read(root, "www/data/System.json"))!;
        Assert.Equal("Savaş", sys["terms"]!["commands"]![0]!.GetValue<string>());
        Assert.Equal("Test Quest", sys["gameTitle"]!.GetValue<string>());
    }

    [Fact]
    public async Task Install_RewrapsLongTranslation()
    {
        var root = TestGames.CreateRpgMv();
        var longTr = "\\N[1] bu çok uzun bir çeviri cümlesidir ve mesaj penceresine sığması için " +
                     "birden fazla satıra bölünmesi gerekir çünkü Türkçe metin çoğunlukla daha uzundur.";
        Assert.True(longTr.Length >= 130);
        await Install(root, NoFont(), new Dictionary<string, string> { ["Hello \\N[1].\nHow are you?"] = longTr });

        var lines = CommandList(root, "www/").Where(c => c!["code"]!.GetValue<int>() == 401).ToList();
        Assert.True(lines.Count >= 4);
        Assert.All(lines, c => Assert.Equal(0, c!["indent"]!.GetValue<int>()));
        var texts = lines.Select(c => c!["parameters"]![0]!.GetValue<string>()).ToList();
        Assert.All(texts, t => Assert.True(PlaceholderGuard.VisibleLength(t, EngineKind.RpgMakerMV) <= 40, t));
        Assert.Equal(longTr, string.Join(" ", texts));
        // 401 grubundan sonraki komutlar yerinde.
        var codes = CommandList(root, "www/").Select(c => c!["code"]!.GetValue<int>()).ToList();
        Assert.Equal(102, codes[1 + lines.Count]);
    }

    [Fact]
    public void Wrap_KeepsExplicitLineBreaks()
    {
        var w = RpgMakerPatcher.Wrap("Kısa satır\nİkinci satır", 40, EngineKind.RpgMakerMV);
        Assert.Equal(new[] { "Kısa satır", "İkinci satır" }, w);
    }

    [Fact]
    public async Task Install_PreservesEncodingWithoutBom()
    {
        var root = TestGames.CreateRpgMv();
        await Install(root, NoFont(), new Dictionary<string, string> { ["Potion"] = "ğüşıöçİ" });
        var bytes = File.ReadAllBytes(Path.Combine(root, "www", "data", "Items.json"));
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Contains("ğüşıöçİ", TestGames.Read(root, "www/data/Items.json"));
    }

    [Fact]
    public async Task Install_PreservesBom_WhenPresent()
    {
        var root = TestGames.CreateRpgMv();
        TestGames.Write(root, "www/data/Items.json", TestGames.Items, bom: true);
        await Install(root, NoFont(), new Dictionary<string, string> { ["Potion"] = "İksir" });
        var bytes = File.ReadAllBytes(Path.Combine(root, "www", "data", "Items.json"));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3));
    }

    [Fact]
    public async Task Install_OnlyTouchesChangedFiles()
    {
        var root = TestGames.CreateRpgMv();
        var before = TestGames.Snapshot(root);
        await Install(root, NoFont(), new Dictionary<string, string> { ["Potion"] = "İksir" });
        var after = TestGames.Snapshot(root);
        Assert.Equal(before["www/data/Map001.json"], after["www/data/Map001.json"]);
        Assert.Equal(before["www/data/System.json"], after["www/data/System.json"]);
        Assert.NotEqual(before["www/data/Items.json"], after["www/data/Items.json"]);
    }

    [Fact]
    public async Task Install_Mv_ReplacesFontCss()
    {
        var root = TestGames.CreateRpgMv();
        await Install(root, new RpgMakerPatcher { FontSourcePath = FakeFont() }, new Dictionary<string, string>());
        Assert.Contains("url(\"gamesubtr-tr.ttf\")", TestGames.Read(root, "www/fonts/gamefont.css"));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(Path.Combine(root, "www", "fonts", "gamesubtr-tr.ttf")));
    }

    [Fact]
    public async Task Install_Mz_SetsMainFontFilename()
    {
        var root = TestGames.CreateRpgMz();
        await Install(root, new RpgMakerPatcher { FontSourcePath = FakeFont() }, new Dictionary<string, string>());
        var sys = JsonNode.Parse(TestGames.Read(root, "data/System.json"))!;
        Assert.Equal("gamesubtr-tr.ttf", sys["advanced"]!["mainFontFilename"]!.GetValue<string>());
        Assert.Equal(26, sys["advanced"]!["fontSize"]!.GetValue<int>());
        Assert.True(File.Exists(Path.Combine(root, "fonts", "gamesubtr-tr.ttf")));
    }

    [Fact]
    public async Task Install_SkipsFont_WhenSourceMissing()
    {
        var root = TestGames.CreateRpgMv();
        var before = TestGames.Snapshot(root);
        await Install(root, new RpgMakerPatcher { FontSourcePath = Path.Combine(root, "yok.ttf") }, new Dictionary<string, string>());
        var after = TestGames.Snapshot(root);
        Assert.Equal(before["www/fonts/gamefont.css"], after["www/fonts/gamefont.css"]);
        Assert.False(after.ContainsKey("www/fonts/gamesubtr-tr.ttf"));
    }

    [Fact]
    public async Task Uninstall_RestoresAllHashes()
    {
        foreach (var root in new[] { TestGames.CreateRpgMv(), TestGames.CreateRpgMz() })
        {
            var before = TestGames.Snapshot(root);
            var backup = await Install(root, new RpgMakerPatcher { FontSourcePath = FakeFont() }, new Dictionary<string, string>
            {
                ["Hello \\N[1].\nHow are you?"] = "Merhaba \\N[1].\nNasılsın?",
                ["Potion"] = "İksir",
                ["Fight"] = "Savaş"
            });
            Assert.NotEqual(before, TestGames.Snapshot(root));
            var r = backup.Uninstall();
            Assert.Empty(r.Conflicts);
            Assert.Equal(before, TestGames.Snapshot(root));
        }
    }

    [Fact]
    public async Task Extract_SkipsBrokenJson()
    {
        var root = TestGames.CreateRpgMv();
        TestGames.Write(root, "www/data/Weapons.json", "{ bozuk");
        var units = await Extract(root);
        Assert.Contains(units, u => u.Source == "Potion");
    }
}
