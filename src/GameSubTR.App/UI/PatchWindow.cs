using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GameSubTR.Core.Logging;
using GameSubTR.Core.Patching;
using Forms = System.Windows.Forms;

namespace GameSubTR.App.UI;

/// <summary>
/// Kalıcı Türkçe yama: oyunun metinlerini dışa aktar → Google Translate → içe aktar.
/// </summary>
public sealed class PatchWindow : Window
{
    private readonly TextBox _game = new() { IsReadOnly = true };
    private readonly TextBox _work = new() { IsReadOnly = true };
    private readonly TextBox _log = new()
    {
        IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontFamily = new FontFamily("Consolas"), FontSize = 12, MinHeight = 220
    };
    private readonly Button _export, _import, _restore, _openWork, _openGoogle;

    private static string WorkRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "GameSubTR", "Yamalar");

    public PatchWindow()
    {
        Title = "GameSubTR — Kalıcı Türkçe yama";
        Width = 760; Height = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;

        Button Btn(string text, RoutedEventHandler click, bool primary = false)
        {
            var b = new Button { Content = text, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };
            if (primary) b.FontWeight = FontWeights.SemiBold;
            b.Click += click;
            return b;
        }

        var pickGame = Btn("Oyun klasörünü seç…", (_, _) => PickGame());
        _export = Btn("1) Metinleri dışa aktar", async (_, _) => await ExportAsync(), primary: true);
        _openWork = Btn("Klasörü aç", (_, _) => OpenFolder(_work.Text));
        _openGoogle = Btn("Google Translate (Dokümanlar)", (_, _) => Open("https://translate.google.com/?sl=en&tl=tr&op=docs"));
        _import = Btn("2) Çeviriyi içe aktar…", async (_, _) => await ImportAsync(), primary: true);
        _restore = Btn("Yamayı geri al", (_, _) => RestorePatch());

        var p = new StackPanel { Margin = new Thickness(14) };
        p.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), Foreground = Brushes.DimGray,
            Text = "Oyunun metin dosyalarını (JSON, CSV, XML, INI/TXT, PO, Ren'Py) bulur ve her satıra bir metin olacak şekilde " +
                   "metinler.txt'ye yazar. Google Translate ile çevirip içe aktarınca her satır oyundaki doğru yerine yazılır. " +
                   "Orijinal dosyalar yedeklenir; 'Yamayı geri al' ile her şey eski haline döner."
        });
        p.Children.Add(Row("Oyun klasörü", _game, pickGame));
        p.Children.Add(Row("Çalışma klasörü", _work, _openWork));
        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 10) };
        actions.Children.Add(_export); actions.Children.Add(_openGoogle); actions.Children.Add(_import); actions.Children.Add(_restore);
        p.Children.Add(actions);
        p.Children.Add(_log);
        Content = new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        UpdateButtons();
    }

    private static Grid Row(string label, TextBox box, Button button)
    {
        var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(box, 1); Grid.SetColumn(button, 2);
        button.Margin = new Thickness(8, 0, 0, 0);
        g.Children.Add(l); g.Children.Add(box); g.Children.Add(button);
        return g;
    }

    private string ProjectFile => Path.Combine(_work.Text, PatchProject.ProjectFileName);

    private void UpdateButtons()
    {
        bool hasGame = Directory.Exists(_game.Text);
        _export.IsEnabled = hasGame;
        _openWork.IsEnabled = Directory.Exists(_work.Text);
        _import.IsEnabled = hasGame && File.Exists(ProjectFile);
        _restore.IsEnabled = hasGame && Directory.Exists(Path.Combine(_game.Text, PatchProject.BackupFolderName));
    }

    private void Write(string line)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _log.AppendText(line + Environment.NewLine);
            _log.ScrollToEnd();
        });
        Log.Info("[Yama] " + line);
    }

    private void SetBusy(bool busy) => Dispatcher.Invoke(() =>
    {
        foreach (var b in new[] { _export, _import, _restore }) b.IsEnabled = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
        if (!busy) UpdateButtons();
    });

    private void PickGame()
    {
        using var dlg = new Forms.FolderBrowserDialog
        {
            Description = "Oyunun kurulu olduğu klasörü seçin (ör. Steam\\steamapps\\common\\OyunAdı)",
            UseDescriptionForTitle = true
        };
        if (dlg.ShowDialog() != Forms.DialogResult.OK) return;
        _game.Text = dlg.SelectedPath;
        var name = string.Concat(Path.GetFileName(dlg.SelectedPath.TrimEnd('\\', '/')).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        _work.Text = Path.Combine(WorkRoot, string.IsNullOrWhiteSpace(name) ? "oyun" : name);
        if (File.Exists(ProjectFile)) Write("Bu oyun için önceki dışa aktarım bulundu. Çeviriyi doğrudan içe aktarabilirsin.");
        UpdateButtons();
    }

    private async Task ExportAsync()
    {
        string game = _game.Text, work = _work.Text;
        SetBusy(true);
        Write($"Taranıyor: {game}");
        try
        {
            var progress = new Progress<string>(_ => { });
            var (_, s) = await Task.Run(() => PatchProject.Export(game, work, progress));
            if (s.Lines == 0)
            {
                Write("Çevrilebilir metin bulunamadı. Bu oyun metinlerini paketlenmiş/ikili dosyalarda tutuyor olabilir " +
                      "(Unity .assets, Unreal .pak/.locres vb.). Oyunun adını bana yazarsan o formata destek ekleyebilirim.");
            }
            else
            {
                Write($"Bitti: {s.Files} dosya, {s.Lines} satır, {s.Characters:N0} karakter ({string.Join(", ", s.ByFormat.Select(kv => $"{kv.Key}: {kv.Value}"))}).");
                Write($"→ {Path.Combine(work, PatchProject.SourceFileName)}  (tamamı)");
                Write($"→ parca_001.txt … parca_{s.Parts:000}.txt  (kopyala-yapıştır için ≤{PatchProject.PartLimit} karakterlik parçalar)");
                Write("Sonraki adım: Google Translate → Dokümanlar → metinler.txt yükle → çevrilmiş dosyayı indir → '2) Çeviriyi içe aktar'.");
                Write("Ayrıntılar çalışma klasöründeki NASIL.txt dosyasında.");
                OpenFolder(work);
            }
            foreach (var sk in s.Skipped.Take(20)) Write("  atlandı: " + sk);
        }
        catch (Exception ex)
        {
            Write("HATA: " + ex.Message);
            Log.Error("Yama dışa aktarımı", ex);
        }
        finally { SetBusy(false); }
    }

    private async Task ImportAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Çevrilmiş dosya(lar)ı seçin — parçalar ise hepsini seçin",
            Filter = "Metin dosyaları (*.txt)|*.txt|Tüm dosyalar|*.*",
            Multiselect = true,
            InitialDirectory = Directory.Exists(_work.Text) ? _work.Text : null
        };
        if (dlg.ShowDialog(this) != true) return;
        var files = dlg.FileNames
            .Where(f => !Path.GetFileName(f).Equals(PatchProject.SourceFileName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0) { Write("İngilizce metinler.txt seçildi; lütfen çevrilmiş dosyayı seçin."); return; }

        string projectFile = ProjectFile;
        SetBusy(true);
        Write($"İçe aktarılıyor: {string.Join(", ", files.Select(Path.GetFileName))}");
        try
        {
            var project = PatchProject.Load(projectFile);
            if (!string.Equals(Path.GetFullPath(project.GameFolder), Path.GetFullPath(_game.Text), StringComparison.OrdinalIgnoreCase))
                project.GameFolder = _game.Text; // oyun taşınmışsa
            var lines = PatchProject.ReadTranslation(files);
            var r = await Task.Run(() => project.Apply(lines));
            Write($"Bitti: {r.Applied} satır Türkçe yazıldı, {r.Unchanged} satır İngilizce kaldı, {r.FilesWritten} dosya güncellendi.");
            foreach (var pr in r.Problems.Take(40)) Write("  ! " + pr);
            if (r.Problems.Count > 40) Write($"  … ve {r.Problems.Count - 40} uyarı daha (log dosyasında).");
            foreach (var pr in r.Problems.Skip(40)) Log.Warn("[Yama] " + pr);
            Write("Oyunu açıp deneyebilirsin. Türkçe harfler (ğ, ş, ı) kutu görünüyorsa oyunun fontu desteklemiyor demektir.");
        }
        catch (Exception ex)
        {
            Write("HATA: " + ex.Message);
            Log.Error("Yama içe aktarımı", ex);
        }
        finally { SetBusy(false); }
    }

    private void RestorePatch()
    {
        if (MessageBox.Show(this, "Oyunun orijinal (İngilizce) dosyaları geri yüklensin mi?", "Yamayı geri al",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            int n = PatchProject.Restore(_game.Text);
            Write($"Geri alındı: {n} dosya orijinal haline döndü.");
        }
        catch (Exception ex) { Write("HATA: " + ex.Message); }
        UpdateButtons();
    }

    private static void OpenFolder(string path)
    {
        if (Directory.Exists(path)) Process.Start("explorer.exe", path);
    }

    private static void Open(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
