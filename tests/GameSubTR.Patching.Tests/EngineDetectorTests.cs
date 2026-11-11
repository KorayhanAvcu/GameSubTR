using Xunit;

namespace GameSubTR.Patching.Tests;

public class EngineDetectorTests
{
    [Fact]
    public void Detects_RenPy()
    {
        var g = EngineDetector.Detect(TestGames.CreateRenPy());
        Assert.Equal(EngineKind.RenPy, g.Engine);
        Assert.EndsWith("game", g.DataDir);
        Assert.Null(g.UnsupportedReason);
        Assert.Equal("RenPyGame", g.Name);
        Assert.False(g.AntiCheat);
    }

    [Fact]
    public void RenPy_CompiledOnly_IsUnsupported()
    {
        var root = TestGames.CreateRenPy();
        File.Move(Path.Combine(root, "game", "script.rpy"), Path.Combine(root, "game", "script.rpyc"));
        var g = EngineDetector.Detect(root);
        Assert.Equal(EngineKind.RenPy, g.Engine);
        Assert.Contains("derlenmiş", g.UnsupportedReason);
    }

    [Fact]
    public void Detects_RpgMakerMV_WithWww()
    {
        var g = EngineDetector.Detect(TestGames.CreateRpgMv());
        Assert.Equal(EngineKind.RpgMakerMV, g.Engine);
        Assert.EndsWith("www", g.DataDir);
        Assert.Null(g.UnsupportedReason);
    }

    [Fact]
    public void Detects_RpgMakerMV_WithoutWww()
    {
        var root = TestGames.CreateRpgMv(www: false);
        var g = EngineDetector.Detect(root);
        Assert.Equal(EngineKind.RpgMakerMV, g.Engine);
        Assert.Equal(g.RootDir, g.DataDir);
    }

    [Fact]
    public void Detects_RpgMakerMZ()
    {
        var g = EngineDetector.Detect(TestGames.CreateRpgMz());
        Assert.Equal(EngineKind.RpgMakerMZ, g.Engine);
        Assert.Equal(g.RootDir, g.DataDir);
    }

    [Fact]
    public void Unity_IsRecognizedButUnsupported()
    {
        var root = TestGames.CreateEmpty();
        TestGames.Write(root, "UnityPlayer.dll", "x");
        TestGames.Write(root, "Game_Data/app.info", "x");
        var g = EngineDetector.Detect(root);
        Assert.Equal(EngineKind.Unity, g.Engine);
        Assert.NotNull(g.UnsupportedReason);
    }

    [Fact]
    public void Unreal_IsRecognizedButUnsupported()
    {
        var root = TestGames.CreateEmpty();
        TestGames.Write(root, "Engine/Binaries/Win64/x.dll", "x");
        TestGames.Write(root, "MyGame/Content/Paks/MyGame-WindowsNoEditor.pak", "x");
        var g = EngineDetector.Detect(root);
        Assert.Equal(EngineKind.Unreal, g.Engine);
        Assert.NotNull(g.UnsupportedReason);
    }

    [Fact]
    public void Unknown_ForEmptyFolder()
    {
        var g = EngineDetector.Detect(TestGames.CreateEmpty());
        Assert.Equal(EngineKind.Unknown, g.Engine);
        Assert.Contains("tanınamadı", g.UnsupportedReason);
    }

    [Fact]
    public void Flags_AntiCheat()
    {
        var root = TestGames.CreateRenPy();
        TestGames.Write(root, "EasyAntiCheat/settings.json", "{}");
        Assert.True(EngineDetector.Detect(root).AntiCheat);
    }
}
