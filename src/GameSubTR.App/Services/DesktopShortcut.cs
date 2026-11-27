using GameSubTR.Core.Logging;

namespace GameSubTR.App.Services;

/// <summary>Masaüstünde "GameSubTR" kısayolu oluşturur / günceller (WScript.Shell).</summary>
public static class DesktopShortcut
{
    public static string ShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "GameSubTR.lnk");

    private static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "GameSubTR.exe");

    /// <summary>Kısayol yoksa ya da başka bir exe'yi gösteriyorsa (klasör taşınmışsa) oluşturur.</summary>
    public static void EnsureExists()
    {
        try
        {
            if (File.Exists(ShortcutPath) &&
                string.Equals(ReadTarget(ShortcutPath), ExePath, StringComparison.OrdinalIgnoreCase))
                return;
            Create();
        }
        catch (Exception ex)
        {
            Log.Warn("Masaüstü kısayolu oluşturulamadı: " + ex.Message);
        }
    }

    public static void Create()
    {
        var t = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell yok");
        dynamic shell = Activator.CreateInstance(t)!;
        try
        {
            dynamic lnk = shell.CreateShortcut(ShortcutPath);
            lnk.TargetPath = ExePath;
            lnk.WorkingDirectory = Path.GetDirectoryName(ExePath);
            lnk.IconLocation = ExePath + ",0";
            lnk.Description = "GameSubTR - Oyun altyazı çevirici";
            lnk.Save();
            Log.Info("Masaüstü kısayolu oluşturuldu: " + ShortcutPath);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }

    private static string? ReadTarget(string path)
    {
        var t = Type.GetTypeFromProgID("WScript.Shell");
        if (t == null) return null;
        dynamic shell = Activator.CreateInstance(t)!;
        try { return (string)shell.CreateShortcut(path).TargetPath; }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
    }
}
