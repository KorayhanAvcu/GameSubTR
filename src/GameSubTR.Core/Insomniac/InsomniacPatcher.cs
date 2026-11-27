using System.Text;
using System.Text.Json;
using GameSubTR.Core.Patching;

namespace GameSubTR.Core.Insomniac;

public sealed class InsomniacLine
{
    public string Text { get; set; } = "";
    public List<string> Keys { get; set; } = new();
}

public sealed class InsomniacProject
{
    public const string ProjectFileName = "proje.insomniac.json";
    public const string TableFileName = "ceviri_tablosu.csv";
    public int Version { get; set; } = 1;
    public string GameFolder { get; set; } = "";
    public string SourceToc { get; set; } = "";
    public List<InsomniacLine> Lines { get; set; } = new();
}

public sealed class InsomniacExportSummary
{
    public int Entries { get; set; }
    public int Lines { get; set; }
    public int Characters { get; set; }
    public int Parts { get; set; }
    public int Documents { get; set; }
    public int ExistingTranslated { get; set; }
    public string SourceToc { get; set; } = "";
    public string OutputFolder { get; set; } = "";
}

/// <summary>
/// Insomniac PC oyunları (Marvel's Spider-Man Remastered, Miles Morales) için kalıcı Türkçe yama.
/// Metinler localization_all.localization varlığındadır (her dil için ayrı span). Yama, çevrilmiş
/// varlıkları ayrı bir arşive yazar ve toc'u bu arşive yönlendirir; oyunun kendi arşivlerine dokunmaz.
/// </summary>
public static class InsomniacPatcher
{
    public const string ArchiveName = "GameSubTR\\localization_tr";
    public const string PreviousTocBackup = "toc.gamesubtr_onceki";
    public const int DocumentLimit = 900_000; // Google Translate doküman çevirisi için güvenli parça boyutu
    private static readonly int[] EnglishSpans = { 8, 0 };

    public static string AssetDir(string gameFolder) => Path.Combine(gameFolder, "asset_archive");

    public static bool IsInsomniacGame(string gameFolder) => Toc.IsToc(Path.Combine(AssetDir(gameFolder), "toc"));

    /// <summary>Bir toc'un metin varlıkları oyunun kendi arşivlerinde mi (yamasız mı)?</summary>
    private static bool PointsToStockArchives(Toc toc)
    {
        foreach (var (_, idx) in toc.FindAsset(LocalizationAsset.AssetId))
        {
            var name = toc.ArchiveName((int)toc.ArchiveIndex(idx));
            if (name.Contains('\\') || name.Contains('/')) return false;
        }
        return true;
    }

    /// <summary>Orijinal (İngilizce) metinleri okuyabileceğimiz yamasız toc'u bulur.</summary>
    public static string? FindCleanToc(string gameFolder)
    {
        var dir = AssetDir(gameFolder);
        var candidates = new List<string> { Path.Combine(dir, PreviousTocBackup), Path.Combine(dir, "toc") };
        candidates.AddRange(Directory.GetFiles(dir, "toc*").Where(f => !candidates.Contains(f)).OrderBy(f => f));
        foreach (var c in candidates)
        {
            if (!File.Exists(c) || !Toc.IsToc(c)) continue;
            try { if (PointsToStockArchives(Toc.Load(c))) return c; } catch { /* sonraki */ }
        }
        return null;
    }

    private static (LocalizationAsset Asset, int Span) ReadEnglish(Toc toc, string dir)
    {
        var all = toc.FindAsset(LocalizationAsset.AssetId);
        if (all.Count == 0) throw new InvalidDataException("Oyunda metin varlığı (localization_all) bulunamadı.");
        foreach (var span in EnglishSpans)
        {
            var hit = all.FirstOrDefault(x => x.Span == span);
            if (hit != default) return (LocalizationAsset.Parse(toc.ReadAsset(dir, hit.Index)), span);
        }
        return (LocalizationAsset.Parse(toc.ReadAsset(dir, all[0].Index)), all[0].Span);
    }

    private static bool Translatable(string v)
    {
        if (string.IsNullOrWhiteSpace(v) || v == "<blank>") return false;
        int letters = v.Count(char.IsLetter);
        return letters >= 2;
    }

    // ----------------------------------------------------------------- dışa aktar

    public static InsomniacExportSummary Export(string gameFolder, string outDir, IProgress<string>? progress = null)
    {
        var dir = AssetDir(gameFolder);
        var cleanPath = FindCleanToc(gameFolder)
            ?? throw new InvalidDataException("Oyunun orijinal toc dosyası bulunamadı. Steam'de 'Dosya bütünlüğünü doğrula' yapıp tekrar deneyin.");
        progress?.Report("Orijinal metinler okunuyor: " + Path.GetFileName(cleanPath));
        var clean = Toc.Load(cleanPath);
        var (en, _) = ReadEnglish(clean, dir);

        // Oyunda halihazırda bir Türkçe yama varsa, mevcut çevirileri referans olarak ekle.
        Dictionary<string, string> existing = new(StringComparer.Ordinal);
        var curPath = Path.Combine(dir, "toc");
        if (!string.Equals(Path.GetFullPath(curPath), Path.GetFullPath(cleanPath), StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var cur = Toc.Load(curPath);
                if (!PointsToStockArchives(cur))
                {
                    progress?.Report("Mevcut Türkçe yama okunuyor");
                    var (tr, _) = ReadEnglish(cur, dir);
                    for (int i = 0; i < tr.Keys.Count; i++) existing[tr.Keys[i]] = tr.Values[i];
                }
            }
            catch { /* mevcut yama okunamazsa referanssız devam */ }
        }

        var project = new InsomniacProject { GameFolder = gameFolder, SourceToc = Path.GetFileName(cleanPath) };
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var table = new StringBuilder();
        table.Append('﻿').AppendLine("ID;Ingilizce;Mevcut_Turkce;Turkce");
        int existingCount = 0;
        for (int i = 0; i < en.Keys.Count; i++)
        {
            var key = en.Keys[i];
            var val = en.Values[i];
            if (!Translatable(val)) continue;
            existing.TryGetValue(key, out var ex);
            if (ex == val) ex = null;
            if (ex != null) existingCount++;
            table.Append(Csv(key)).Append(';').Append(Csv(val)).Append(';').Append(Csv(ex ?? "")).Append(';').AppendLine();

            var line = PatchProject.Encode(val);
            if (!index.TryGetValue(line, out var li))
            {
                li = project.Lines.Count;
                index[line] = li;
                project.Lines.Add(new InsomniacLine { Text = line });
            }
            project.Lines[li].Keys.Add(key);
        }

        Directory.CreateDirectory(outDir);
        foreach (var old in Directory.GetFiles(outDir, "parca_*.txt").Concat(Directory.GetFiles(outDir, "dokuman_*.txt"))) File.Delete(old);
        var lines = project.Lines.Select(l => l.Text).ToList();
        var utf8 = new UTF8Encoding(false);
        File.WriteAllText(Path.Combine(outDir, PatchProject.SourceFileName), string.Join("\n", lines), utf8);
        File.WriteAllText(Path.Combine(outDir, InsomniacProject.TableFileName), table.ToString(), utf8);
        int parts = WriteChunks(outDir, "parca", lines, PatchProject.PartLimit);
        int docs = WriteChunks(outDir, "dokuman", lines, DocumentLimit);
        File.WriteAllText(Path.Combine(outDir, InsomniacProject.ProjectFileName), JsonSerializer.Serialize(project), utf8);

        var summary = new InsomniacExportSummary
        {
            Entries = project.Lines.Sum(l => l.Keys.Count), Lines = lines.Count, Characters = lines.Sum(l => l.Length),
            Parts = parts, Documents = docs, ExistingTranslated = existingCount, SourceToc = project.SourceToc, OutputFolder = outDir
        };
        File.WriteAllText(Path.Combine(outDir, "NASIL.txt"), HowTo(summary), new UTF8Encoding(true));
        return summary;
    }

    private static string Csv(string s) =>
        s.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    private static int WriteChunks(string folder, string prefix, List<string> lines, int limit)
    {
        int part = 0, i = 0;
        while (i < lines.Count)
        {
            var sb = new StringBuilder();
            while (i < lines.Count && (sb.Length == 0 || sb.Length + lines[i].Length + 1 <= limit))
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(lines[i++]);
            }
            File.WriteAllText(Path.Combine(folder, $"{prefix}_{++part:000}.txt"), sb.ToString(), new UTF8Encoding(false));
        }
        return part;
    }

    private static string HowTo(InsomniacExportSummary s) =>
$@"GameSubTR — Spider-Man Türkçe yama
{s.Entries:N0} metin, {s.Lines:N0} benzersiz satır, {s.Characters:N0} karakter.
Mevcut yamada çevrilmiş olan: {s.ExistingTranslated:N0} metin (ceviri_tablosu.csv'de 'Mevcut_Turkce' sütununda).

İKİ YOL VAR — hangisi kolayına gelirse:

A) GOOGLE TRANSLATE (satır satır)
   - translate.google.com → Dokümanlar → dokuman_001.txt, dokuman_002.txt ... dosyalarını sırayla yükle,
     çevrilen dosyaları indir (adları dokuman_001_tr.txt gibi olsun).
   - Ya da parca_001.txt ... dosyalarının içeriğini kopyala-yapıştır yap (her biri <5000 karakter).
   - GameSubTR → Kalıcı Türkçe yama → '2) Çeviriyi içe aktar' → çevrilmiş dosyaların HEPSİNİ birlikte seç.
   Kural: satır sayısını değiştirme, '¶' işaretini ve <ts=...>, <br>, [EMJ_...] gibi kodları silme.

B) TABLO (ID'ye göre)
   - ceviri_tablosu.csv'yi Excel ile aç. 'Turkce' sütununu doldur (boş bırakılan satır çevrilmez).
   - Kaydet (CSV olarak) → '2) Çeviriyi içe aktar' → ceviri_tablosu.csv'yi seç.

NOT: Mevcut yamada ğ, ş, ı, İ yerine g, s, i, I kullanılmış (oyun fontu bu harfleri göstermeyebilir).
GameSubTR bu dönüşümü içe aktarırken otomatik yapar; istersen kapatabilirsin.
Oyunu kapatıp yamayı uygula. Steam 'dosya bütünlüğünü doğrula' yaparsa yama gider, tekrar uygularsın.
";

    // ----------------------------------------------------------------- içe aktar

    public static InsomniacProject LoadProject(string file) =>
        JsonSerializer.Deserialize<InsomniacProject>(File.ReadAllText(file)) ?? throw new InvalidDataException("Proje okunamadı");

    /// <summary>Çeviri dosyalarını okur: .csv ise ID'ye göre, değilse satır sırasına göre.</summary>
    public static Dictionary<string, string> ReadTranslations(InsomniacProject project, IReadOnlyList<string> files, List<string> problems)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var csv = files.Where(f => f.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)).ToList();
        var txt = files.Except(csv).ToList();

        foreach (var c in csv) ReadCsv(c, result, problems);

        if (txt.Count > 0)
        {
            var lines = PatchProject.ReadTranslation(txt);
            if (lines.Count != project.Lines.Count)
                throw new InvalidDataException($"Satır sayısı tutmuyor: beklenen {project.Lines.Count:N0}, gelen {lines.Count:N0}. " +
                    "Tüm parçaları (ya da tüm doküman dosyalarını) birlikte seçtiğinizden ve satırların birleşmediğinden emin olun.");
            for (int i = 0; i < lines.Count; i++)
            {
                var raw = lines[i];
                if (string.IsNullOrWhiteSpace(raw) || raw.Trim() == project.Lines[i].Text) continue;
                var tr = PatchProject.Decode(raw);
                foreach (var k in project.Lines[i].Keys) result.TryAdd(k, tr);
            }
        }
        return result;
    }

    private static void ReadCsv(string path, Dictionary<string, string> into, List<string> problems)
    {
        var (text, _) = TextFile.Read(path);
        var rows = ParseCsv(text.TrimStart('﻿'), text.Contains(';') ? ';' : ',');
        if (rows.Count == 0) return;
        var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int id = header.IndexOf("id");
        int tr = header.FindIndex(h => h is "turkce" or "türkçe" or "tr");
        if (id < 0 || tr < 0) { problems.Add($"{Path.GetFileName(path)}: 'ID' ve 'Turkce' sütunları bulunamadı"); return; }
        foreach (var r in rows.Skip(1))
            if (r.Count > Math.Max(id, tr) && !string.IsNullOrWhiteSpace(r[tr])) into[r[id].Trim()] = r[tr];
    }

    private static List<List<string>> ParseCsv(string s, char d)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var sb = new StringBuilder();
        bool q = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (q)
            {
                if (c == '"') { if (i + 1 < s.Length && s[i + 1] == '"') { sb.Append('"'); i++; } else q = false; }
                else sb.Append(c);
            }
            else if (c == '"' && sb.Length == 0) q = true;
            else if (c == d) { row.Add(sb.ToString()); sb.Clear(); }
            else if (c == '\n') { row.Add(sb.ToString().TrimEnd('\r')); sb.Clear(); rows.Add(row); row = new List<string>(); }
            else sb.Append(c);
        }
        if (sb.Length > 0 || row.Count > 0) { row.Add(sb.ToString()); rows.Add(row); }
        return rows;
    }

    /// <summary>Oyun fontunda olmayabilecek harfleri en yakın karşılığına çevirir.</summary>
    public static string Transliterate(string s) =>
        s.Replace('ğ', 'g').Replace('Ğ', 'G').Replace('ş', 's').Replace('Ş', 'S').Replace('ı', 'i').Replace('İ', 'I');

    public sealed class ApplyOptions
    {
        /// <summary>ğ→g, ş→s, ı→i, İ→I (mevcut yamalarda olduğu gibi).</summary>
        public bool Transliterate { get; set; } = true;
        /// <summary>Senin çevirmediğin metinler için oyundaki mevcut Türkçe yamayı kullan.</summary>
        public bool KeepExistingTranslations { get; set; } = true;
    }

    public static ImportReport Apply(string gameFolder, InsomniacProject project, Dictionary<string, string> translations, ApplyOptions options, IProgress<string>? progress = null)
    {
        var report = new ImportReport();
        var dir = AssetDir(gameFolder);
        var tocPath = Path.Combine(dir, "toc");
        var cleanPath = FindCleanToc(gameFolder) ?? throw new InvalidDataException("Orijinal toc bulunamadı.");
        var clean = Toc.Load(cleanPath);
        var (en, _) = ReadEnglish(clean, dir);
        var english = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < en.Keys.Count; i++) english[en.Keys[i]] = en.Values[i];

        // Mevcut Türkçe yama (senin çevirmediklerin için).
        var final = new Dictionary<string, string>(StringComparer.Ordinal);
        if (options.KeepExistingTranslations && !PointsToStockArchivesSafe(tocPath))
        {
            try
            {
                var cur = Toc.Load(tocPath);
                var (tr, _) = ReadEnglish(cur, dir);
                for (int i = 0; i < tr.Keys.Count; i++)
                    if (english.TryGetValue(tr.Keys[i], out var e) && e != tr.Values[i]) final[tr.Keys[i]] = tr.Values[i];
            }
            catch (Exception ex) { report.Problems.Add("Mevcut yama okunamadı: " + ex.Message); }
        }

        foreach (var (key, value) in translations)
        {
            if (!english.TryGetValue(key, out var src)) { report.Unchanged++; continue; }
            var t = Placeholders.Repair(src, value);
            if (!Placeholders.Preserved(src, t))
            {
                report.Unchanged++;
                if (report.Problems.Count < 200) report.Problems.Add($"{key}: kod parçası bozuldu → İngilizce bırakıldı");
                continue;
            }
            final[key] = t;
            report.Applied++;
        }
        if (options.Transliterate)
            foreach (var k in final.Keys.ToList()) final[k] = Transliterate(final[k]);

        // Her dil span'i için: orijinal varlık + çeviriler → yeni arşiv.
        var outDir = Path.Combine(dir, "GameSubTR");
        Directory.CreateDirectory(outDir);
        var archivePath = Path.Combine(outDir, "localization_tr");
        var newToc = Toc.Load(cleanPath);
        uint archiveIndex = newToc.FindOrAddArchive(ArchiveName);
        var tmpArchive = archivePath + ".gstr.tmp";
        using (var fs = File.Create(tmpArchive))
        {
            foreach (var (span, idx) in clean.FindAsset(LocalizationAsset.AssetId))
            {
                progress?.Report($"Dil varyantı yazılıyor (span {span})");
                var asset = LocalizationAsset.Parse(clean.ReadAsset(dir, idx));
                var bytes = asset.SaveWithValues(final);
                uint offset = (uint)fs.Position;
                fs.Write(bytes);
                newToc.SetLocation(idx, archiveIndex, offset, (uint)bytes.Length);
                report.FilesWritten++;
            }
        }

        // Yamadan önceki toc'u bir kez yedekle (geri alma için).
        var prev = Path.Combine(dir, PreviousTocBackup);
        if (!File.Exists(prev)) File.Copy(tocPath, prev);
        File.Move(tmpArchive, archivePath, overwrite: true);
        newToc.Save(tocPath);
        return report;
    }

    private static bool PointsToStockArchivesSafe(string tocPath)
    {
        try { return PointsToStockArchives(Toc.Load(tocPath)); } catch { return true; }
    }

    /// <summary>GameSubTR yamasını kaldırır: toc yamadan önceki haline döner.</summary>
    public static bool Restore(string gameFolder)
    {
        var dir = AssetDir(gameFolder);
        var prev = Path.Combine(dir, PreviousTocBackup);
        if (!File.Exists(prev)) return false;
        File.Copy(prev, Path.Combine(dir, "toc"), overwrite: true);
        File.Delete(prev);
        var ours = Path.Combine(dir, "GameSubTR");
        if (Directory.Exists(ours)) Directory.Delete(ours, recursive: true);
        return true;
    }

    public static bool IsPatched(string gameFolder) => File.Exists(Path.Combine(AssetDir(gameFolder), PreviousTocBackup));
}
