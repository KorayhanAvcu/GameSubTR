using System.Text.Json;
using GameSubTR.Patching.RenPy;
using Xunit;

namespace GameSubTR.Patching.Tests;

public class RenPyTests
{
    private static string[] Sources(string script) => RenPyScriptParser.Parse(script, "s.rpy").Select(u => u.Source).ToArray();

    [Fact]
    public void Parse_Dialogue_Narration_Menu_Strings()
    {
        var units = RenPyScriptParser.Parse(TestGames.RenPyScript, "script.rpy").ToList();
        Assert.Equal(new[] { "Hello, [player]!", "It was a dark night.", "Nice \"day\".", "Go left", "Start" }, units.Select(u => u.Source));
        Assert.Equal(new[] { "e", null, "e", "menu", "string" }, units.Select(u => u.Context));
        Assert.Equal("script.rpy:2", units[0].Id);
    }

    [Fact]
    public void Parse_SkipsPythonBlocksAndComments()
    {
        var s = Sources("""
            # e "commented out"
            init python:
                x = "python string"
                config.name = _("Game Name")
            label a:
                e "Real line" # trailing comment
                python:
                    y = "also python"
                e 'Single quoted'
                play music "audio/theme.ogg"
                scene bg "room.png"
                extend "More."
            """);
        Assert.Equal(new[] { "Game Name", "Real line", "Single quoted", "More." }, s);
    }

    [Fact]
    public void Parse_ScreenText()
    {
        var s = Sources("""
            screen main_menu():
                style_prefix "main"
                add "bg.png"
                text "Welcome back"
                textbutton "Load Game" action ShowMenu("load")
                textbutton _("Quit") action Quit()
            label start:
                "After screen."
            """);
        Assert.Equal(new[] { "Welcome back", "Load Game", "Quit", "After screen." }, s);
    }

    [Fact]
    public void Parse_SkipsTripleQuoted()
    {
        var s = Sources("label a:\n    e \"\"\"\n    Long\n    text\n    \"\"\"\n    e \"After\"\n");
        Assert.Equal(new[] { "After" }, s);
    }

    [Fact]
    public void Parse_UnescapesNewline_AndCollapsesSpaces()
    {
        var s = Sources("label a:\n    e \"A\\nB\"\n    e \"Two  spaces\"\n");
        Assert.Equal(new[] { "A\nB", "Two spaces" }, s);
    }

    [Fact]
    public async Task Extract_HandlesBomAndCrlf()
    {
        var root = TestGames.CreateRenPy(game =>
        {
            File.Delete(Path.Combine(game, "script.rpy"));
        });
        TestGames.Write(root, "game/script.rpy", "\"First line.\"\r\nlabel a:\r\n    e \"Second line.\"\r\n", bom: true);
        var units = await new RenPyPatcher().ExtractAsync(EngineDetector.Detect(root), default);
        Assert.Equal(new[] { "First line.", "Second line." }, units.Select(u => u.Source));
    }

    [Fact]
    public async Task Extract_IgnoresTlFolderAndOwnFile()
    {
        var root = TestGames.CreateRenPy();
        TestGames.Write(root, "game/tl/french/script.rpy", "label x:\n    e \"Bonjour\"\n");
        TestGames.Write(root, "game/" + RenPyPatcher.ScriptName, "label y:\n    e \"own\"\n");
        TestGames.Write(root, "game/sub/extra.rpy", "label z:\n    e \"Extra\"\n");
        var units = await new RenPyPatcher().ExtractAsync(EngineDetector.Detect(root), default);
        Assert.DoesNotContain(units, u => u.Source is "Bonjour" or "own");
        Assert.Contains(units, u => u.Source == "Extra" && u.Id == "sub/extra.rpy:2");
    }

    [Fact]
    public async Task Install_AddsTwoFiles_AndUninstallRemovesThem()
    {
        var root = TestGames.CreateRenPy();
        var before = TestGames.Snapshot(root);
        var game = EngineDetector.Detect(root);
        var backup = new BackupManager(root);

        await new RenPyPatcher().InstallAsync(game, new Dictionary<string, string> { ["Go left"] = "Sola git", ["Hello, [player]!"] = "Merhaba, [player]!" }, backup, default);
        backup.Commit();

        var after = TestGames.Snapshot(root);
        Assert.Equal(before["game/script.rpy"], after["game/script.rpy"]);
        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(TestGames.Read(root, "game/gamesubtr_tr.json"))!;
        Assert.Equal("Sola git", map["Go left"]);
        var script = TestGames.Read(root, "game/zz_gamesubtr_tr.rpy");
        Assert.Contains("config.say_menu_text_filter = _gstr_filter", script);
        Assert.Contains("config.replace_text = _gstr_replace", script);
        Assert.Contains("renpy.file(\"gamesubtr_tr.json\")", script);
        Assert.Contains("define gui.text_font = \"DejaVuSans.ttf\"", script);
        Assert.DoesNotContain("__", script.Replace("__init__", ""));
        Assert.DoesNotContain('\r', script);

        backup.Uninstall();
        Assert.Equal(before, TestGames.Snapshot(root));
    }

    [Fact]
    public void Install_WithoutFonts_OmitsGuiDefines()
    {
        var script = RenPyPatcher.BuildScript(replaceFonts: false);
        Assert.DoesNotContain("gui.", script);
        Assert.Contains("init offset = -1\ninit 999 python:", script);
    }

    [Fact]
    public async Task Install_WritesTurkishCharactersUnescaped()
    {
        var root = TestGames.CreateRenPy();
        var backup = new BackupManager(root);
        await new RenPyPatcher().InstallAsync(EngineDetector.Detect(root), new Dictionary<string, string> { ["Go left"] = "Şuraya \"git\", İğde" }, backup, default);
        var bytes = File.ReadAllBytes(Path.Combine(root, "game", "gamesubtr_tr.json"));
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Contains("Şuraya \\\"git\\\", İğde", TestGames.Read(root, "game/gamesubtr_tr.json"));
    }
}
