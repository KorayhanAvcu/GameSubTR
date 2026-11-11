using System.Text;
using Xunit;

namespace GameSubTR.Patching.Tests;

public class BackupManagerTests
{
    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    private static string NewGame()
    {
        var root = TestGames.NewDir();
        TestGames.Write(root, "data/a.txt", "original A");
        TestGames.Write(root, "data/b.txt", "original B");
        return root;
    }

    [Fact]
    public void Roundtrip_RestoresOriginalBytes()
    {
        var root = NewGame();
        var before = TestGames.Snapshot(root);

        var bm = new BackupManager(root);
        Assert.False(bm.IsInstalled);
        bm.WriteFile("data/a.txt", B("patched"));
        bm.WriteFile("new/sub/c.txt", B("added"));
        bm.Commit();
        Assert.True(bm.IsInstalled);
        Assert.Equal("patched", TestGames.Read(root, "data/a.txt"));

        var r = new BackupManager(root).Uninstall();
        Assert.Equal(1, r.Restored);
        Assert.Equal(1, r.Removed);
        Assert.Empty(r.Conflicts);
        Assert.Equal(before, TestGames.Snapshot(root));
        Assert.False(Directory.Exists(Path.Combine(root, BackupManager.DirName)));
        Assert.False(Directory.Exists(Path.Combine(root, "new")));
    }

    [Fact]
    public void SecondWrite_KeepsFirstBackup()
    {
        var root = NewGame();
        var bm = new BackupManager(root);
        bm.WriteFile("data/a.txt", B("v1"));
        bm.WriteFile("data/a.txt", B("v2"));
        bm.Commit();
        bm.Uninstall();
        Assert.Equal("original A", TestGames.Read(root, "data/a.txt"));
    }

    [Fact]
    public void Uninstall_WithoutCommit_StillRestores()
    {
        var root = NewGame();
        var before = TestGames.Snapshot(root);
        var bm = new BackupManager(root);
        bm.WriteFile("data/a.txt", B("half"));
        bm.WriteFile("x.txt", B("added"));
        // Commit yok: süreç çökmüş gibi yeni bir örnekle geri al.
        var r = new BackupManager(root).Uninstall();
        Assert.Empty(r.Conflicts);
        Assert.Equal(before, TestGames.Snapshot(root));
    }

    [Fact]
    public void Uninstall_ReportsConflict_WhenFileChangedAfterPatch()
    {
        var root = NewGame();
        var bm = new BackupManager(root);
        bm.WriteFile("data/a.txt", B("patched"));
        bm.WriteFile("data/b.txt", B("patched B"));
        bm.Commit();
        TestGames.Write(root, "data/a.txt", "game update");

        var r = new BackupManager(root).Uninstall();
        Assert.Equal(new[] { "data/a.txt" }, r.Conflicts);
        Assert.Equal("game update", TestGames.Read(root, "data/a.txt"));
        Assert.Equal("original B", TestGames.Read(root, "data/b.txt"));
        Assert.True(new BackupManager(root).IsInstalled);

        var forced = new BackupManager(root).Uninstall(force: true);
        Assert.Equal(1, forced.Restored);
        Assert.Equal("original A", TestGames.Read(root, "data/a.txt"));
        Assert.False(Directory.Exists(Path.Combine(root, BackupManager.DirName)));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("data/../../outside.txt")]
    [InlineData("C:\\Windows\\x.txt")]
    [InlineData(".gamesubtr-patch/manifest.json")]
    public void WriteFile_RejectsPathOutsideRoot(string path)
    {
        var bm = new BackupManager(NewGame());
        Assert.Throws<ArgumentException>(() => bm.WriteFile(path, B("x")));
    }
}
