using System.Diagnostics;
using GameSubTR.Core.Logging;
using GameSubTR.Patching.RenPy;
using GameSubTR.Patching.RpgMaker;

namespace GameSubTR.Patching;

public sealed record PatchReport(int TotalUnits, int Translated, int Skipped, int Failed, IReadOnlyList<string> FailedSamples);

/// <summary>Uçtan uca yama akışı: ön kontrol → çıkarma → çeviri → kurulum (hata olursa geri alma) → rapor.</summary>
public sealed class PatchSession
{
    public const string AntiCheatMessage = "Bu oyunda anti-cheat algılandı; yama kurulmaz.";
    public const string RunningMessage = "Oyun çalışıyor. Kapatıp tekrar dene.";
    public const string NoWriteMessage = "Oyun klasörüne yazılamıyor. Patcher'ı yönetici olarak çalıştır.";
    public const string ChangedMessage = "Oyun dosyaları yamadan sonra değişmiş (oyun güncellenmiş olabilir). Önce \"Yamayı kaldır\" ile eski yamayı kaldır.";
    public const string InstalledMessage = "Bu oyunda yama kurulu. Metinleri yeniden çıkarmak için önce yamayı kaldır.";

    private readonly IReadOnlyList<IGamePatcher> _patchers;
    private readonly Func<GameInfo, BatchTranslator> _translatorFactory;

    public PatchSession(IEnumerable<IGamePatcher> patchers, Func<GameInfo, BatchTranslator> translatorFactory)
    {
        _patchers = patchers.ToList();
        _translatorFactory = translatorFactory;
    }

    public static IReadOnlyList<IGamePatcher> DefaultPatchers(bool replaceFonts = true) => new IGamePatcher[]
    {
        new RenPyPatcher { ReplaceFonts = replaceFonts },
        new RpgMakerPatcher { ReplaceFont = replaceFonts }
    };

    /// <summary>Kurulumu engelleyen durumun açıklaması; sorun yoksa null.</summary>
    public static string? PreflightError(GameInfo game)
    {
        if (game.UnsupportedReason != null) return game.UnsupportedReason;
        if (game.AntiCheat) return AntiCheatMessage;
        if (IsRunning(game.RootDir)) return RunningMessage;
        if (!CanWrite(game.RootDir)) return NoWriteMessage;
        return null;
    }

    public static bool IsInstalled(GameInfo game) => new BackupManager(game.RootDir).IsInstalled;

    public Task<IReadOnlyList<TextUnit>> ExtractAsync(GameInfo game, CancellationToken ct)
    {
        if (game.UnsupportedReason != null) throw new PatchException(game.UnsupportedReason);
        // Kurulu yamanın üstünden çıkarma, çevrilmiş metni kaynak sanar.
        if (IsInstalled(game)) throw new PatchException(InstalledMessage);
        return PatcherFor(game).ExtractAsync(game, ct);
    }

    public async Task<PatchReport> InstallAsync(GameInfo game, IReadOnlyList<TextUnit> units,
        IReadOnlyDictionary<string, string> glossary, IProgress<BatchProgress>? progress, CancellationToken ct)
    {
        var error = PreflightError(game);
        if (error != null) throw new PatchException(error);
        var patcher = PatcherFor(game);

        var previous = new BackupManager(game.RootDir);
        if (previous.IsInstalled && previous.Uninstall(force: false).Conflicts.Count > 0)
            throw new PatchException(ChangedMessage);

        var batch = await _translatorFactory(game)
            .TranslateAsync(units, game.Engine, game.Name, glossary, progress, ct).ConfigureAwait(false);

        var backup = new BackupManager(game.RootDir);
        try
        {
            await patcher.InstallAsync(game, batch.Translations, backup, ct).ConfigureAwait(false);
            backup.Commit();
        }
        catch (Exception ex)
        {
            Log.Error("Kurulum yarıda kaldı, geri alınıyor", ex);
            try { backup.Uninstall(force: true); }
            catch (Exception rollbackEx) { Log.Error("Geri alma başarısız", rollbackEx); }
            throw;
        }

        var total = units.Select(u => u.Source).Distinct(StringComparer.Ordinal).Count();
        Log.Info($"Yama kuruldu: {game.Name} ({game.Engine}) — {batch.Translations.Count}/{total} çevrildi, {batch.Failed.Count} hata, {batch.Skipped} atlandı");
        return new PatchReport(total, batch.Translations.Count, batch.Skipped, batch.Failed.Count, batch.Failed.Take(20).ToList());
    }

    public UninstallResult Uninstall(GameInfo game, bool force = false)
    {
        if (IsRunning(game.RootDir)) throw new PatchException(RunningMessage);
        var r = new BackupManager(game.RootDir).Uninstall(force);
        Log.Info($"Yama kaldırıldı: {game.Name} — {r.Restored} geri yüklendi, {r.Removed} silindi, {r.Conflicts.Count} çakışma");
        return r;
    }

    private IGamePatcher PatcherFor(GameInfo game) =>
        _patchers.FirstOrDefault(p => p.CanHandle(game.Engine))
        ?? throw new PatchException(game.UnsupportedReason ?? EngineDetector.LaterEngineReason);

    private static bool IsRunning(string root)
    {
        var prefix = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var file = p.MainModule?.FileName;
                if (file != null && file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { /* erişilemeyen sistem süreçleri */ }
            finally { p.Dispose(); }
        }
        return false;
    }

    private static bool CanWrite(string root)
    {
        try
        {
            var probe = Path.Combine(root, ".gamesubtr-write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return false; }
    }
}
