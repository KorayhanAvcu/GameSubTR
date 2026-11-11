namespace GameSubTR.Patching;

/// <summary>Oyun klasörüne bakıp motoru tanır. Dosya değiştirmez.</summary>
public static class EngineDetector
{
    public const string CompiledRenPyReason = "Betikler derlenmiş (.rpyc/.rpa); bu sürümde desteklenmiyor.";
    public const string LaterEngineReason = "Bu motor sonraki sürümde desteklenecek.";
    public const string UnknownReason = "Oyun motoru tanınamadı. Canlı çeviri için GameSubTR'yi kullanabilirsin.";

    public static GameInfo Detect(string rootDir)
    {
        var root = Path.GetFullPath(rootDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(root);
        if (!Directory.Exists(root))
            return new GameInfo(root, name, EngineKind.Unknown, root, false, "Klasör bulunamadı.");

        var antiCheat = HasAntiCheat(root);
        GameInfo Info(EngineKind e, string dataDir, string? reason = null) => new(root, name, e, dataDir, antiCheat, reason);

        // Ren'Py
        var gameDir = Path.Combine(root, "game");
        if (Directory.Exists(Path.Combine(root, "renpy")) && Directory.Exists(gameDir))
        {
            var files = SafeEnumerate(gameDir, "*", SearchOption.AllDirectories).ToList();
            bool HasExt(string ext) => files.Any(f => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
            if (HasExt(".rpy")) return Info(EngineKind.RenPy, gameDir);
            if (HasExt(".rpyc") || HasExt(".rpa")) return Info(EngineKind.RenPy, gameDir, CompiledRenPyReason);
        }

        // RPG Maker MV / MZ
        var www = Path.Combine(root, "www");
        if (File.Exists(Path.Combine(www, "data", "System.json")) && File.Exists(Path.Combine(www, "js", "rpg_core.js")))
            return Info(EngineKind.RpgMakerMV, www);
        if (File.Exists(Path.Combine(root, "data", "System.json")))
        {
            if (File.Exists(Path.Combine(root, "js", "rmmz_core.js"))) return Info(EngineKind.RpgMakerMZ, root);
            if (File.Exists(Path.Combine(root, "js", "rpg_core.js"))) return Info(EngineKind.RpgMakerMV, root);
        }

        // Unity
        if (File.Exists(Path.Combine(root, "UnityPlayer.dll")) &&
            SafeDirectories(root).Any(d => d.EndsWith("_Data", StringComparison.OrdinalIgnoreCase)))
            return Info(EngineKind.Unity, root, LaterEngineReason);

        // Unreal
        if (Directory.Exists(Path.Combine(root, "Engine", "Binaries")) &&
            SafeDirectories(root).Any(d =>
            {
                var paks = Path.Combine(d, "Content", "Paks");
                return Directory.Exists(paks) && SafeEnumerate(paks, "*.pak", SearchOption.TopDirectoryOnly).Any();
            }))
            return Info(EngineKind.Unreal, root, LaterEngineReason);

        return Info(EngineKind.Unknown, root, UnknownReason);
    }

    private static bool HasAntiCheat(string root)
    {
        foreach (var dir in new[] { root }.Concat(SafeDirectories(root)))
        {
            var dn = Path.GetFileName(dir);
            if (dir != root && (dn.Equals("EasyAntiCheat", StringComparison.OrdinalIgnoreCase) || dn.Equals("BattlEye", StringComparison.OrdinalIgnoreCase)))
                return true;
            foreach (var f in SafeEnumerate(dir, "*", SearchOption.TopDirectoryOnly))
            {
                var fn = Path.GetFileName(f);
                var isExe = fn.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
                if (isExe && (fn.Contains("EAC", StringComparison.Ordinal) || fn.Contains("EasyAntiCheat", StringComparison.OrdinalIgnoreCase)
                              || fn.StartsWith("BEService", StringComparison.OrdinalIgnoreCase)))
                    return true;
                if (fn.Equals("vgk.sys", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    private static IEnumerable<string> SafeDirectories(string dir)
    {
        try { return Directory.GetDirectories(dir); } catch { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> SafeEnumerate(string dir, string pattern, SearchOption opt)
    {
        try { return Directory.GetFiles(dir, pattern, opt); } catch { return Array.Empty<string>(); }
    }
}
