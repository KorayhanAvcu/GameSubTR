using Xunit;

namespace GameSubTR.Patching.Tests;

public class SteamLibraryTests
{
    private static string Acf(string name, string dir) =>
        $"\"AppState\"\n{{\n\t\"appid\"\t\t\"1\"\n\t\"name\"\t\t\"{name}\"\n\t\"installdir\"\t\t\"{dir}\"\n}}\n";

    [Fact]
    public void FindIn_ReadsMultipleLibraries()
    {
        var steam = TestGames.NewDir("Steam");
        var lib2 = TestGames.NewDir("Lib2");
        TestGames.Write(steam, "steamapps/libraryfolders.vdf",
            "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"" + steam.Replace(@"\", @"\\") + "\"\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"" + lib2.Replace(@"\", @"\\") + "\"\n\t}\n}\n");
        TestGames.Write(steam, "steamapps/appmanifest_10.acf", Acf("Zeta Game", "Zeta"));
        Directory.CreateDirectory(Path.Combine(steam, "steamapps", "common", "Zeta"));
        TestGames.Write(lib2, "steamapps/appmanifest_20.acf", Acf("Alpha Game", "Alpha"));
        Directory.CreateDirectory(Path.Combine(lib2, "steamapps", "common", "Alpha"));

        var games = SteamLibrary.FindIn(steam);
        Assert.Equal(new[] { "Alpha Game", "Zeta Game" }, games.Select(g => g.Name));
        Assert.Equal(Path.Combine(lib2, "steamapps", "common", "Alpha"), games[0].Path);
    }

    [Fact]
    public void FindIn_SkipsMissingInstallDir()
    {
        var steam = TestGames.NewDir("Steam");
        TestGames.Write(steam, "steamapps/appmanifest_10.acf", Acf("Ghost", "Ghost"));
        Assert.Empty(SteamLibrary.FindIn(steam));
    }

    [Fact]
    public void FindIn_ReturnsEmpty_WhenNoSteamapps() =>
        Assert.Empty(SteamLibrary.FindIn(TestGames.NewDir("NoSteam")));

    [Fact]
    public void FindIn_ToleratesGarbageFiles()
    {
        var steam = TestGames.NewDir("Steam");
        TestGames.Write(steam, "steamapps/libraryfolders.vdf", "çöp \"path\" \"Z:\\\\yok\\\\burada\" {{{");
        TestGames.WriteBytes(steam, "steamapps/appmanifest_1.acf", new byte[] { 0, 255, 1, 2 });
        TestGames.Write(steam, "steamapps/appmanifest_2.acf", Acf("Real", "Real"));
        Directory.CreateDirectory(Path.Combine(steam, "steamapps", "common", "Real"));
        Assert.Equal(new[] { "Real" }, SteamLibrary.FindIn(steam).Select(g => g.Name));
    }
}
