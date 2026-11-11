using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace GameSubTR.Patching;

public sealed record InstalledGame(string Name, string Path);

/// <summary>Steam kütüphanelerindeki kurulu oyunları listeler (yalnızca okur).</summary>
public static partial class SteamLibrary
{
    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex PathRegex();

    [GeneratedRegex("\"name\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex NameRegex();

    [GeneratedRegex("\"installdir\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex InstallDirRegex();

    public static IReadOnlyList<InstalledGame> Find()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<InstalledGame>();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue("SteamPath") is string root && root.Length > 0 ? FindIn(root) : Array.Empty<InstalledGame>();
        }
        catch { return Array.Empty<InstalledGame>(); }
    }

    public static IReadOnlyList<InstalledGame> FindIn(string steamRoot)
    {
        var games = new Dictionary<string, InstalledGame>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var libraries = new List<string> { steamRoot };
            var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
                foreach (Match m in PathRegex().Matches(File.ReadAllText(vdf)))
                    libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));

            foreach (var lib in libraries.Select(l => l.Replace('/', '\\').TrimEnd('\\')).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var apps = Path.Combine(lib, "steamapps");
                if (!Directory.Exists(apps)) continue;
                foreach (var acf in Directory.GetFiles(apps, "appmanifest_*.acf"))
                {
                    try
                    {
                        var text = File.ReadAllText(acf);
                        var dir = InstallDirRegex().Match(text);
                        if (!dir.Success || dir.Groups[1].Value.Length == 0) continue;
                        var path = Path.Combine(apps, "common", dir.Groups[1].Value);
                        if (!Directory.Exists(path)) continue;
                        var name = NameRegex().Match(text);
                        games[path] = new InstalledGame(name.Success && name.Groups[1].Value.Length > 0 ? name.Groups[1].Value : dir.Groups[1].Value, path);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* bozuk/erişilemeyen kayıt */ }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { /* yoksay */ }
        return games.Values.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
