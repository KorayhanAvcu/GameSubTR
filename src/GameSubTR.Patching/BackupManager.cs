using System.Security.Cryptography;
using System.Text.Json;

namespace GameSubTR.Patching;

public sealed record UninstallResult(int Restored, int Removed, IReadOnlyList<string> Conflicts);

/// <summary>
/// Yamanın oyun klasöründe yaptığı her değişikliği kaydeder ve geri alır.
/// Düzen: &lt;kök&gt;/.gamesubtr-patch/manifest.json + backup/&lt;göreli yol&gt;.
/// </summary>
public sealed class BackupManager
{
    public const string DirName = ".gamesubtr-patch";

    private sealed class Entry
    {
        public string Path { get; set; } = "";
        public string Action { get; set; } = "";
        public string? Sha256Before { get; set; }
        public string? Sha256After { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _root;
    private readonly string _dir;
    private readonly string _manifestPath;
    private readonly List<Entry> _entries;

    public BackupManager(string gameRoot)
    {
        _root = Path.GetFullPath(gameRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _dir = Path.Combine(_root, DirName);
        _manifestPath = Path.Combine(_dir, "manifest.json");
        _entries = LoadManifest();
    }

    public bool IsInstalled => File.Exists(_manifestPath);

    /// <summary>Dosyayı yazar; var olan dosyanın ilk hali yedeklenir (modified), yoksa eklenmiş (added) sayılır.</summary>
    public void WriteFile(string relativePath, byte[] content)
    {
        var rel = Normalize(relativePath);
        var full = FullPath(rel);
        var entry = _entries.FirstOrDefault(e => e.Path.Equals(rel, StringComparison.OrdinalIgnoreCase));
        if (entry == null)
        {
            entry = new Entry { Path = rel };
            if (File.Exists(full))
            {
                var backup = BackupPath(rel);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Copy(full, backup, overwrite: true);
                entry.Action = "modified";
                entry.Sha256Before = Hash(full);
            }
            else entry.Action = "added";
            _entries.Add(entry);
            SaveManifest(); // yazmadan önce: yarıda kesilirse geri alınabilsin
        }
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        entry.Sha256After = null;
    }

    /// <summary>Kurulum bitti: son özetleri kaydeder.</summary>
    public void Commit()
    {
        foreach (var e in _entries)
        {
            var full = FullPath(e.Path);
            e.Sha256After = File.Exists(full) ? Hash(full) : null;
        }
        if (_entries.Count > 0) SaveManifest();
    }

    /// <summary>
    /// Yamayı geri alır. Yamadan sonra değişmiş dosyalar çakışma sayılır; <paramref name="force"/> değilse dokunulmaz.
    /// </summary>
    public UninstallResult Uninstall(bool force = false)
    {
        int restored = 0, removed = 0;
        var conflicts = new List<string>();
        var remaining = new List<Entry>();

        foreach (var e in _entries)
        {
            var full = FullPath(e.Path);
            var exists = File.Exists(full);
            var changed = e.Sha256After != null && (!exists || !Hash(full).Equals(e.Sha256After, StringComparison.OrdinalIgnoreCase));
            if (changed)
            {
                conflicts.Add(e.Path);
                if (!force) { remaining.Add(e); continue; }
            }

            if (e.Action == "added")
            {
                if (exists) { File.Delete(full); removed++; }
                RemoveEmptyParents(full);
            }
            else
            {
                var backup = BackupPath(e.Path);
                if (File.Exists(backup))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    File.Copy(backup, full, overwrite: true);
                    restored++;
                }
            }
        }

        _entries.Clear();
        _entries.AddRange(remaining);
        if (remaining.Count == 0)
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        else SaveManifest();

        return new UninstallResult(restored, removed, conflicts);
    }

    private void RemoveEmptyParents(string fullFilePath)
    {
        var dir = Path.GetDirectoryName(fullFilePath);
        while (!string.IsNullOrEmpty(dir) && dir.Length > _root.Length && Directory.Exists(dir)
               && !Directory.EnumerateFileSystemEntries(dir).Any())
        {
            Directory.Delete(dir);
            dir = Path.GetDirectoryName(dir);
        }
    }

    private string Normalize(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new ArgumentException("Yol oyun klasörüne göreli olmalı: " + relativePath, nameof(relativePath));
        var full = Path.GetFullPath(Path.Combine(_root, relativePath));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Yol oyun klasörünün dışına çıkıyor: " + relativePath, nameof(relativePath));
        var rel = Path.GetRelativePath(_root, full).Replace('\\', '/');
        if (rel.StartsWith(DirName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Yedek klasörüne yazılamaz: " + relativePath, nameof(relativePath));
        return rel;
    }

    private string FullPath(string rel) => Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
    private string BackupPath(string rel) => Path.Combine(_dir, "backup", rel.Replace('/', Path.DirectorySeparatorChar));

    private static string Hash(string file)
    {
        using var s = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    private List<Entry> LoadManifest()
    {
        if (!File.Exists(_manifestPath)) return new List<Entry>();
        try { return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_manifestPath), Json) ?? new List<Entry>(); }
        catch (JsonException) { throw new PatchException("Yama kaydı (manifest.json) bozuk: " + _manifestPath); }
    }

    private void SaveManifest()
    {
        Directory.CreateDirectory(_dir);
        var tmp = _manifestPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_entries, Json));
        File.Move(tmp, _manifestPath, overwrite: true);
    }
}
