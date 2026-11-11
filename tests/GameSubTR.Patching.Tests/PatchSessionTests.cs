using System.Text;
using Xunit;

namespace GameSubTR.Patching.Tests;

public class PatchSessionTests
{
    private static readonly Dictionary<string, string> NoGlossary = new();

    private static PatchSession Session(FakeTranslator t, IEnumerable<IGamePatcher>? patchers = null)
    {
        var storeDir = TestGames.NewDir("store");
        return new PatchSession(patchers ?? PatchSession.DefaultPatchers(replaceFonts: false),
            _ => new BatchTranslator(FakeTranslator.Service(t), new TranslationStore(Path.Combine(storeDir, "t.jsonl"))));
    }

    private static async Task EndToEnd(string root, string translatedFile, string expectedFragment)
    {
        var before = TestGames.Snapshot(root);
        var game = EngineDetector.Detect(root);
        var session = Session(new FakeTranslator(FakeTranslator.Good));

        var units = await session.ExtractAsync(game, default);
        Assert.NotEmpty(units);
        var report = await session.InstallAsync(game, units, NoGlossary, null, default);
        Assert.True(report.Translated > 0);
        Assert.Equal(0, report.Failed);
        Assert.True(PatchSession.IsInstalled(game));
        Assert.Contains(expectedFragment, TestGames.Read(root, translatedFile));

        var r = session.Uninstall(game);
        Assert.Empty(r.Conflicts);
        Assert.Equal(before, TestGames.Snapshot(root));
        Assert.False(PatchSession.IsInstalled(game));
    }

    [Fact]
    public Task EndToEnd_RenPy_InstallThenUninstall_RestoresFolder() =>
        EndToEnd(TestGames.CreateRenPy(), "game/gamesubtr_tr.json", "TR:Hello, [player]!");

    [Fact]
    public Task EndToEnd_RpgMakerMv_InstallThenUninstall_RestoresFolder() =>
        EndToEnd(TestGames.CreateRpgMv(), "www/data/Map001.json", "TR:Hello \\\\N[1].");

    [Fact]
    public Task EndToEnd_RpgMakerMz_InstallThenUninstall_RestoresFolder() =>
        EndToEnd(TestGames.CreateRpgMz(), "data/Items.json", "TR:Potion");

    [Fact]
    public async Task Install_Twice_DoesNotStackBackups()
    {
        var root = TestGames.CreateRpgMv();
        var before = TestGames.Snapshot(root);
        var game = EngineDetector.Detect(root);
        var session = Session(new FakeTranslator(FakeTranslator.Good));
        var units = await session.ExtractAsync(game, default);

        await session.InstallAsync(game, units, NoGlossary, null, default);
        await session.InstallAsync(game, units, NoGlossary, null, default);
        Assert.DoesNotContain("TR:TR:", TestGames.Read(root, "www/data/Items.json"));

        session.Uninstall(game);
        Assert.Equal(before, TestGames.Snapshot(root));
    }

    [Fact]
    public async Task Extract_Refuses_WhenPatchInstalled()
    {
        var root = TestGames.CreateRpgMv();
        var game = EngineDetector.Detect(root);
        var session = Session(new FakeTranslator(FakeTranslator.Good));
        await session.InstallAsync(game, await session.ExtractAsync(game, default), NoGlossary, null, default);
        var ex = await Assert.ThrowsAsync<PatchException>(() => session.ExtractAsync(game, default));
        Assert.Equal(PatchSession.InstalledMessage, ex.Message);
    }

    private sealed class ThrowingPatcher : IGamePatcher
    {
        public bool CanHandle(EngineKind engine) => true;
        public Task<IReadOnlyList<TextUnit>> ExtractAsync(GameInfo game, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TextUnit>>(new[] { new TextUnit("a", "Some text", null) });
        public Task InstallAsync(GameInfo game, IReadOnlyDictionary<string, string> translations, BackupManager backup, CancellationToken ct)
        {
            backup.WriteFile("game/script.rpy", Encoding.UTF8.GetBytes("bozuldu"));
            backup.WriteFile("game/new.rpy", Encoding.UTF8.GetBytes("yeni"));
            throw new IOException("disk doldu");
        }
    }

    [Fact]
    public async Task Install_RollsBack_WhenPatcherThrows()
    {
        var root = TestGames.CreateRenPy();
        var before = TestGames.Snapshot(root);
        var game = EngineDetector.Detect(root);
        var session = Session(new FakeTranslator(FakeTranslator.Good), new[] { new ThrowingPatcher() });

        await Assert.ThrowsAsync<IOException>(async () =>
            await session.InstallAsync(game, await session.ExtractAsync(game, default), NoGlossary, null, default));

        Assert.Equal(before, TestGames.Snapshot(root));
        Assert.False(Directory.Exists(Path.Combine(root, BackupManager.DirName)));
    }

    [Fact]
    public async Task Install_Refuses_WhenGameChangedAfterPatch()
    {
        var root = TestGames.CreateRpgMv();
        var game = EngineDetector.Detect(root);
        var session = Session(new FakeTranslator(FakeTranslator.Good));
        var units = await session.ExtractAsync(game, default);
        await session.InstallAsync(game, units, NoGlossary, null, default);
        TestGames.Write(root, "www/data/Items.json", "[null]"); // oyun güncellemesi

        var ex = await Assert.ThrowsAsync<PatchException>(() => session.InstallAsync(game, units, NoGlossary, null, default));
        Assert.Equal(PatchSession.ChangedMessage, ex.Message);
        Assert.Equal("[null]", TestGames.Read(root, "www/data/Items.json"));
    }

    [Fact]
    public async Task Preflight_BlocksAntiCheat_And_Unsupported()
    {
        var ac = TestGames.CreateRenPy();
        TestGames.Write(ac, "EasyAntiCheat/x.json", "{}");
        var acGame = EngineDetector.Detect(ac);
        Assert.Equal(PatchSession.AntiCheatMessage, PatchSession.PreflightError(acGame));

        var unknown = EngineDetector.Detect(TestGames.CreateEmpty());
        Assert.Equal(EngineDetector.UnknownReason, PatchSession.PreflightError(unknown));

        Assert.Null(PatchSession.PreflightError(EngineDetector.Detect(TestGames.CreateRenPy())));

        var session = Session(new FakeTranslator(FakeTranslator.Good));
        var before = TestGames.Snapshot(ac);
        await Assert.ThrowsAsync<PatchException>(() =>
            session.InstallAsync(acGame, new[] { new TextUnit("a", "Some text", null) }, NoGlossary, null, default));
        Assert.Equal(before, TestGames.Snapshot(ac));
    }

    [Fact]
    public async Task Report_CountsFailedAndSkipped()
    {
        var root = TestGames.CreateRenPy(game =>
            File.WriteAllText(Path.Combine(game, "script.rpy"), "label a:\n    e \"Good line\"\n    e \"Bad [name] line\"\n    e \"12345\"\n    e \"Good line\"\n"));
        var game = EngineDetector.Detect(root);
        // Yer tutucuyu her zaman düşüren motor: yer tutuculu birim başarısız olur.
        var t = new FakeTranslator(r => System.Text.RegularExpressions.Regex.Replace(FakeTranslator.Good(r), @"<x\d+/>", ""));
        var session = Session(t);
        var report = await session.InstallAsync(game, await session.ExtractAsync(game, default), NoGlossary, null, default);

        Assert.Equal(3, report.TotalUnits);
        Assert.Equal(1, report.Translated);
        Assert.Equal(1, report.Skipped);
        Assert.Equal(1, report.Failed);
        Assert.Equal(new[] { "Bad [name] line" }, report.FailedSamples);
    }
}
