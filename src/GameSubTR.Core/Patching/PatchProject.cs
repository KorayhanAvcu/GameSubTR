using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GameSubTR.Core.Patching;

public sealed class PatchFile
{
    public string RelPath { get; set; } = "";
    public string Format { get; set; } = "";
    public string Sha256 { get; set; } = "";
}

public sealed class PatchRef
{
    public int File { get; set; }
    public string Id { get; set; } = "";
}

public sealed class PatchLine
{
    public string Text { get; set; } = "";
    public List<PatchRef> Refs { get; set; } = new();
}

public sealed class ExportSummary
{
    public int Files { get; set; }
    public int Lines { get; set; }
    public int Characters { get; set; }
    public int Parts { get; set; }
    public Dictionary<string, int> ByFormat { get; set; } = new();
    public List<string> Skipped { get; set; } = new();
    public string OutputFolder { get; set; } = "";
}

public sealed class ImportReport
{
    public int Applied { get; set; }
    public int Unchanged { get; set; }
    public int FilesWritten { get; set; }
    public List<string> Problems { get; set; } = new();
}

/// <summary>
/// Kalıcı Türkçe yama: oyun klasöründen metinleri tek satır = tek metin olacak şekilde
/// dışa aktarır (Google Translate'e kopyala-yapıştır için), çeviriyi satır sırasına göre
/// aynı yerlere geri yazar. Orijinal dosyalar yedeklenir, geri alınabilir.
/// </summary>
public sealed class PatchProject
{
    public const string ProjectFileName = "proje.gamesubtr.json";
    public const string SourceFileName = "metinler.txt";
    public const string BackupFolderName = "_GameSubTR_yedek";
    public const string NewlineMarker = " ¶ ";
    public const int PartLimit = 4800; // Google Translate metin kutusu sınırı 5000 karakter

    public int Version { get; set; } = 1;
    public string GameFolder { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public List<PatchFile> Files { get; set; } = new();
    public List<PatchLine> Lines { get; set; } = new();

    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        BackupFolderName, ".git", "__pycache__", "MonoBleedingEdge", "D3D12", "Redist", "_CommonRedist", "DirectX", "vcredist"
    };

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>Tek satıra indirger: satır sonları "¶" olur.</summary>
    public static string Encode(string text) => text.Replace("\r\n", "\n").Replace("\r", "\n").Trim().Replace("\n", NewlineMarker);

    public static string Decode(string line)
    {
        var parts = line.Split('¶');
        return string.Join("\n", parts.Select(p => p.Trim()));
    }

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    // ----------------------------------------------------------------- dışa aktar

    public static (PatchProject Project, ExportSummary Summary) Export(string gameFolder, string outputFolder, IProgress<string>? progress = null, long maxFileBytes = 30 * 1024 * 1024)
    {
        gameFolder = Path.GetFullPath(gameFolder);
        var project = new PatchProject { GameFolder = gameFolder };
        var summary = new ExportSummary { OutputFolder = outputFolder };
        var lineIndex = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var path in EnumerateFiles(gameFolder))
        {
            var fmt = TextFormats.For(path);
            if (fmt == null) continue;
            var rel = Path.GetRelativePath(gameFolder, path);
            try
            {
                var info = new FileInfo(path);
                if (info.Length == 0 || info.Length > maxFileBytes || TextFile.LooksBinary(path)) continue;
                var bytes = File.ReadAllBytes(path);
                if (!TryDecode(bytes, out var content, out _)) { summary.Skipped.Add($"{rel}: UTF-8/UTF-16 değil"); continue; }
                progress?.Report(rel);
                var entries = fmt.Extract(content);
                if (entries.Count == 0) continue;

                int fileIdx = project.Files.Count;
                project.Files.Add(new PatchFile { RelPath = rel, Format = fmt.Name, Sha256 = Sha(bytes) });
                summary.ByFormat[fmt.Name] = summary.ByFormat.GetValueOrDefault(fmt.Name) + 1;
                foreach (var e in entries)
                {
                    var encoded = Encode(e.Text);
                    if (!lineIndex.TryGetValue(encoded, out var li))
                    {
                        li = project.Lines.Count;
                        lineIndex[encoded] = li;
                        project.Lines.Add(new PatchLine { Text = encoded });
                    }
                    project.Lines[li].Refs.Add(new PatchRef { File = fileIdx, Id = e.Id });
                }
            }
            catch (Exception ex)
            {
                summary.Skipped.Add($"{rel}: {ex.Message}");
            }
        }

        summary.Files = project.Files.Count;
        summary.Lines = project.Lines.Count;
        summary.Characters = project.Lines.Sum(l => l.Text.Length);

        Directory.CreateDirectory(outputFolder);
        foreach (var old in Directory.GetFiles(outputFolder, "parca_*.txt")) File.Delete(old);
        File.WriteAllText(Path.Combine(outputFolder, SourceFileName), string.Join("\n", project.Lines.Select(l => l.Text)), new UTF8Encoding(false));
        summary.Parts = WriteParts(outputFolder, project.Lines.Select(l => l.Text).ToList());
        File.WriteAllText(Path.Combine(outputFolder, ProjectFileName), JsonSerializer.Serialize(project, Json), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(outputFolder, "NASIL.txt"), HowTo(summary), new UTF8Encoding(true));
        return (project, summary);
    }

    private static int WriteParts(string folder, List<string> lines)
    {
        int part = 0, i = 0;
        while (i < lines.Count)
        {
            var sb = new StringBuilder();
            while (i < lines.Count && (sb.Length == 0 || sb.Length + lines[i].Length + 1 <= PartLimit))
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(lines[i++]);
            }
            part++;
            File.WriteAllText(Path.Combine(folder, $"parca_{part:000}.txt"), sb.ToString(), new UTF8Encoding(false));
        }
        return part;
    }

    private static string HowTo(ExportSummary s) =>
$@"GameSubTR — Türkçe yama ({s.Lines} satır, {s.Characters:N0} karakter, {s.Files} dosya)

YÖNTEM 1 (en kolay): translate.google.com → Dokümanlar → '{SourceFileName}' dosyasını yükle
  → İngilizce → Türkçe → Çevir → çevrilen dosyayı indir.

YÖNTEM 2 (kopyala-yapıştır): parca_001.txt, parca_002.txt ... dosyalarını sırayla aç,
  içeriğini Google Translate'e yapıştır, çeviriyi aynı adla '_tr' ekleyerek kaydet:
  parca_001_tr.txt, parca_002_tr.txt ...

Sonra GameSubTR → Türkçe yama → 'Çeviriyi içe aktar' ile çevrilmiş dosya(lar)ı seç.

KURALLAR
- Satırları birleştirme / silme / yer değiştirme: 1. satır 1. metindir.
- Bir satırı İngilizce bırakmak için o satırı boş bırakabilir ya da hiç değiştirmeyebilirsin.
- '¶' işareti satır içi alt satır demektir, silme.
- {{0}}, %s, <b>, [tag], \n gibi kod parçalarına dokunma (bozulursa o satır İngilizce kalır).
";

    private static IEnumerable<string> EnumerateFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] files, dirs;
            try { files = Directory.GetFiles(dir); dirs = Directory.GetDirectories(dir); }
            catch { continue; }
            foreach (var f in files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) yield return f;
            foreach (var d in dirs.OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase))
                if (!SkipDirs.Contains(Path.GetFileName(d))) stack.Push(d);
        }
    }

    /// <summary>Katı UTF-8 (BOM'lu/BOM'suz) veya UTF-16. Diğer kodlamalar Türkçe karakter taşıyamaz → atlanır.</summary>
    public static bool TryDecode(byte[] bytes, out string text, out Encoding encoding)
    {
        text = ""; encoding = new UTF8Encoding(false);
        try
        {
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) { encoding = new UnicodeEncoding(false, true, true); text = encoding.GetString(bytes, 2, bytes.Length - 2); return true; }
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) { encoding = new UnicodeEncoding(true, true, true); text = encoding.GetString(bytes, 2, bytes.Length - 2); return true; }
            bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            var strict = new UTF8Encoding(bom, true);
            text = strict.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
            encoding = new UTF8Encoding(bom, false);
            return true;
        }
        catch (DecoderFallbackException) { return false; }
    }

    // ----------------------------------------------------------------- içe aktar

    public static PatchProject Load(string projectFile)
    {
        var p = JsonSerializer.Deserialize<PatchProject>(File.ReadAllText(projectFile)) ?? throw new InvalidDataException("Proje dosyası okunamadı");
        return p;
    }

    /// <summary>Çevrilmiş metin dosyalarını (sırayla birleştirerek) satırlara ayırır.</summary>
    public static List<string> ReadTranslation(IEnumerable<string> files)
    {
        var lines = new List<string>();
        foreach (var f in files)
        {
            var (text, _) = TextFile.Read(f);
            var ls = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
            while (ls.Count > 0 && string.IsNullOrWhiteSpace(ls[^1])) ls.RemoveAt(ls.Count - 1); // sondaki boş satırlar
            lines.AddRange(ls);
        }
        return lines;
    }

    public string BackupFolder => Path.Combine(GameFolder, BackupFolderName);

    public ImportReport Apply(IReadOnlyList<string> translatedLines, IProgress<string>? progress = null)
    {
        var report = new ImportReport();
        if (translatedLines.Count != Lines.Count)
            throw new InvalidDataException(
                $"Satır sayısı tutmuyor: beklenen {Lines.Count}, gelen {translatedLines.Count}. " +
                "Çeviri sırasında satırlar birleşmiş ya da bölünmüş olabilir. Dokümanlar sekmesiyle çevirmeyi ya da parçaları sırayla seçmeyi deneyin.");

        var perFile = new Dictionary<int, Dictionary<string, string>>();
        for (int i = 0; i < Lines.Count; i++)
        {
            var src = Lines[i].Text;
            var raw = translatedLines[i];
            if (string.IsNullOrWhiteSpace(raw) || raw.Trim() == src) { report.Unchanged++; continue; }
            var srcText = Decode(src);
            var tr = Placeholders.Repair(srcText, Decode(raw));
            if (!Placeholders.Preserved(srcText, tr))
            {
                report.Unchanged++;
                if (report.Problems.Count < 200) report.Problems.Add($"Satır {i + 1}: kod parçası bozuldu → İngilizce bırakıldı ({Trunc(srcText)})");
                continue;
            }
            report.Applied++;
            foreach (var r in Lines[i].Refs)
            {
                if (!perFile.TryGetValue(r.File, out var d)) perFile[r.File] = d = new Dictionary<string, string>(StringComparer.Ordinal);
                d[r.Id] = tr;
            }
        }

        foreach (var (fi, translations) in perFile)
        {
            var pf = Files[fi];
            var target = Path.Combine(GameFolder, pf.RelPath);
            var backup = Path.Combine(BackupFolder, pf.RelPath);
            progress?.Report(pf.RelPath);
            try
            {
                if (!File.Exists(target)) { report.Problems.Add($"{pf.RelPath}: dosya bulunamadı"); continue; }
                if (!File.Exists(backup))
                {
                    var current = File.ReadAllBytes(target);
                    if (Sha(current) != pf.Sha256)
                        report.Problems.Add($"{pf.RelPath}: dışa aktarımdan sonra değişmiş (oyun güncellenmiş olabilir) — yine de uygulandı");
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(target, backup);
                }
                // Her zaman orijinal (yedek) üzerinden uygula: tekrar içe aktarmada Id'ler kaymaz.
                if (!TryDecode(File.ReadAllBytes(backup), out var content, out var enc)) { report.Problems.Add($"{pf.RelPath}: kodlama okunamadı"); continue; }
                var fmt = TextFormats.All.First(f => f.Name == pf.Format);
                var patched = fmt.Apply(content, translations);
                TextFile.Write(target, patched, enc);
                report.FilesWritten++;
            }
            catch (UnauthorizedAccessException)
            {
                report.Problems.Add($"{pf.RelPath}: yazma izni yok — GameSubTR'yi yönetici olarak çalıştırın");
            }
            catch (Exception ex)
            {
                report.Problems.Add($"{pf.RelPath}: {ex.Message}");
            }
        }
        return report;
    }

    /// <summary>Yedekten orijinal dosyaları geri yükler. Geri yüklenen dosya sayısını döndürür.</summary>
    public static int Restore(string gameFolder)
    {
        var backupRoot = Path.Combine(gameFolder, BackupFolderName);
        if (!Directory.Exists(backupRoot)) return 0;
        int n = 0;
        foreach (var b in Directory.GetFiles(backupRoot, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(backupRoot, b);
            var target = Path.Combine(gameFolder, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(b, target, overwrite: true);
            n++;
        }
        Directory.Delete(backupRoot, recursive: true);
        return n;
    }

    private static string Trunc(string s) => s.Length > 60 ? s[..60] + "…" : s;
}
