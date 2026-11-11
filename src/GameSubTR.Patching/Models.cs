namespace GameSubTR.Patching;

public enum EngineKind { Unknown, RenPy, RpgMakerMV, RpgMakerMZ, Unity, Unreal }

/// <summary>Algılanan oyun. <see cref="DataDir"/> motorun veri kökü (Ren'Py: game, MV: www, MZ: kök).</summary>
public sealed record GameInfo(string RootDir, string Name, EngineKind Engine, string DataDir, bool AntiCheat, string? UnsupportedReason);

/// <summary>Çevrilecek tek metin. <see cref="Source"/> oyunun çalışma zamanında gördüğü metindir.</summary>
public sealed record TextUnit(string Id, string Source, string? Context);

public interface IGamePatcher
{
    bool CanHandle(EngineKind engine);

    Task<IReadOnlyList<TextUnit>> ExtractAsync(GameInfo game, CancellationToken ct);

    /// <param name="translations">Anahtar = <see cref="TextUnit.Source"/>, değer = Türkçe metin. Eksik anahtar = orijinal kalır.</param>
    Task InstallAsync(GameInfo game, IReadOnlyDictionary<string, string> translations, BackupManager backup, CancellationToken ct);
}

public sealed class PatchException(string message) : Exception(message);
