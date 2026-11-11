using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using GameSubTR.Core.Logging;
using GameSubTR.Core.Models;
using GameSubTR.Core.Translation;
using GameSubTR.Patching;
using Microsoft.Win32;

namespace GameSubTR.Patcher;

public partial class MainWindow : Window
{
    private static readonly Dictionary<string, string> NoGlossary = new();

    private readonly HttpClient _http = new();
    private readonly AppSettings _settings;
    private readonly TranslationService _service;

    private GameInfo? _game;
    private IReadOnlyList<TextUnit>? _units;
    private CancellationTokenSource? _cts;

    public MainWindow()
    {
        InitializeComponent();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("GameSubTR/1.0");
        _settings = new SettingsStore(Path.Combine(App.DataDir, "settings.json")).Load();
        _service = TranslationFactory.Create(() => _settings, _http);
        Loaded += async (_, _) =>
        {
            var games = await Task.Run(SteamLibrary.Find);
            SteamList.ItemsSource = games;
            if (games.Count == 0) SteamLabel.Text = "Steam oyunu bulunamadı; klasörü elle seç.";
        };
    }

    // ---------------------------------------------------------------- 1. oyun seç

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Oyunun kurulu olduğu klasörü seç" };
        if (dlg.ShowDialog(this) == true) SelectGame(dlg.FolderName);
    }

    private void OnSteamDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SteamList.SelectedItem is InstalledGame g) SelectGame(g.Path);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = SelectPanel.IsVisible && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (!SelectPanel.IsVisible || e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths) return;
        var path = paths[0];
        if (File.Exists(path)) path = Path.GetDirectoryName(path)!; // exe bırakıldıysa klasörü
        if (Directory.Exists(path)) SelectGame(path);
    }

    // ---------------------------------------------------------------- 2. algılama

    private void SelectGame(string path)
    {
        _game = EngineDetector.Detect(path);
        _units = null;
        Log.Info($"Oyun seçildi: {_game.RootDir} → {_game.Engine}");
        RefreshDetect();
        Show(DetectPanel);
    }

    private void RefreshDetect()
    {
        var game = _game!;
        GameName.Text = game.Name;
        GamePath.Text = game.RootDir;
        EngineText.Text = "Motor: " + EngineName(game.Engine);

        var installed = PatchSession.IsInstalled(game);
        var error = PatchSession.PreflightError(game);
        if (error == null && _service.ActiveChain().Count == 0)
            error = "Çeviri motoru yapılandırılmamış. GameSubTR → Ayarlar → Çeviri bölümünden bir motor ekle.";

        ErrorText.Text = error ?? "";
        ErrorText.Visibility = error != null ? Visibility.Visible : Visibility.Collapsed;
        InstalledText.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        UninstallButton.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;

        var canPatch = error == null && !installed;
        FontCheck.Visibility = canPatch ? Visibility.Visible : Visibility.Collapsed;
        ExtractButton.Visibility = canPatch && _units == null ? Visibility.Visible : Visibility.Collapsed;
        StartButton.Visibility = canPatch && _units != null ? Visibility.Visible : Visibility.Collapsed;
        StatsText.Visibility = canPatch && _units != null ? Visibility.Visible : Visibility.Collapsed;
        ExtractButton.IsEnabled = StartButton.IsEnabled = BackButton.IsEnabled = UninstallButton.IsEnabled = true;
    }

    private async void OnExtract(object sender, RoutedEventArgs e)
    {
        var game = _game!;
        ExtractButton.IsEnabled = BackButton.IsEnabled = false;
        try
        {
            var session = NewSession();
            var units = await Task.Run(() => session.ExtractAsync(game, CancellationToken.None));
            var distinct = units.Select(u => u.Source).Distinct(StringComparer.Ordinal).ToList();
            var store = new TranslationStore(TranslationStore.PathFor(game.RootDir));
            var ready = distinct.Count(s => store.TryGet(s, out _));
            _units = units;
            StatsText.Text =
                $"{distinct.Count:N0} farklı metin bulundu ({distinct.Sum(s => (long)s.Length):N0} karakter).\n" +
                (ready > 0 ? $"{ready:N0} tanesinin çevirisi önceki çalışmadan hazır.\n" : "") +
                $"Çeviri motoru: {_service.ActiveChain()[0].DisplayName}";
            if (distinct.Count == 0)
            {
                _units = null;
                ErrorMessage("Bu oyunda çevrilecek metin bulunamadı.");
            }
        }
        catch (Exception ex) when (ex is PatchException or IOException or UnauthorizedAccessException)
        {
            Log.Error("Metin çıkarma başarısız", ex);
            ErrorMessage(ex.Message);
        }
        RefreshDetect();
    }

    // ---------------------------------------------------------------- 3. çeviri + kurulum

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        var game = _game!;
        var units = _units!;
        var session = NewSession();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Bar.Value = 0;
        ProgressText.Text = "Başlıyor…";
        CancelButton.IsEnabled = true;
        Show(ProgressPanel);

        var progress = new Progress<BatchProgress>(p =>
        {
            Bar.Value = p.Total == 0 ? 1 : (double)p.Done / p.Total;
            ProgressText.Text = $"{p.Done:N0} / {p.Total:N0} çevrildi" + (p.Failed > 0 ? $", {p.Failed:N0} hata" : "");
        });

        try
        {
            var report = await Task.Run(() => session.InstallAsync(game, units, NoGlossary, progress, ct), ct);
            ShowResult(report);
        }
        catch (OperationCanceledException)
        {
            Log.Info("Çeviri iptal edildi");
            RefreshDetect();
            Show(DetectPanel);
        }
        catch (Exception ex) when (ex is PatchException or TranslationException or IOException or UnauthorizedAccessException)
        {
            Log.Error("Yama kurulamadı", ex);
            ErrorMessage("Yama kurulamadı; oyun dosyaları eski haline döndürüldü.\n\n" + ex.Message);
            RefreshDetect();
            Show(DetectPanel);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        ProgressText.Text = "İptal ediliyor…";
        _cts?.Cancel();
    }

    // ---------------------------------------------------------------- 4. sonuç

    private void ShowResult(PatchReport r)
    {
        ResultTitle.Text = "Yama kuruldu";
        ResultText.Text =
            $"{r.Translated:N0} / {r.TotalUnits:N0} metin Türkçeye çevrildi." +
            (r.Skipped > 0 ? $"\n{r.Skipped:N0} metin çeviri gerektirmediği için atlandı (sayı, kod, dosya adı)." : "") +
            (r.Failed > 0 ? $"\n{r.Failed:N0} metin çevrilemedi; bunlar oyunda İngilizce kalır." : "") +
            "\n\nOyunu açıp deneyebilirsin. Sorun olursa \"Yamayı kaldır\" oyunu eski haline döndürür.";
        var hasFailed = r.FailedSamples.Count > 0;
        FailedLabel.Visibility = FailedList.Visibility = hasFailed ? Visibility.Visible : Visibility.Collapsed;
        FailedList.ItemsSource = r.FailedSamples.Select(s => s.Replace("\n", " ⏎ ")).ToList();
        ResultUninstallButton.Visibility = Visibility.Visible;
        Show(ResultPanel);
    }

    private async void OnUninstall(object sender, RoutedEventArgs e)
    {
        var game = _game!;
        var session = NewSession();
        try
        {
            var r = await Task.Run(() => session.Uninstall(game));
            if (r.Conflicts.Count > 0)
            {
                var list = string.Join("\n", r.Conflicts.Take(12)) + (r.Conflicts.Count > 12 ? "\n…" : "");
                var answer = MessageBox.Show(this,
                    "Bu dosyalar yamadan sonra değişmiş (oyun güncellenmiş olabilir):\n\n" + list +
                    "\n\nYine de yama öncesi yedekleri geri yükleyeyim mi? Güncellemeyle gelen değişiklikler bu dosyalarda kaybolur.",
                    "Yamayı kaldır", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) { RefreshDetect(); Show(DetectPanel); return; }
                await Task.Run(() => session.Uninstall(game, force: true));
            }
            MessageBox.Show(this, "Yama kaldırıldı; oyun dosyaları eski haline döndü.", "Yamayı kaldır", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is PatchException or IOException or UnauthorizedAccessException)
        {
            Log.Error("Yama kaldırılamadı", ex);
            ErrorMessage(ex.Message);
        }
        _units = null;
        RefreshDetect();
        Show(DetectPanel);
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        _game = null;
        _units = null;
        Show(SelectPanel);
    }

    private void OnOpenGameFolder(object sender, RoutedEventArgs e)
    {
        if (_game != null) OpenFolder(_game.RootDir);
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e) => OpenFolder(App.LogDir);

    // ---------------------------------------------------------------- yardımcılar

    private PatchSession NewSession() => new(
        PatchSession.DefaultPatchers(replaceFonts: FontCheck.IsChecked == true),
        game => new BatchTranslator(_service, new TranslationStore(TranslationStore.PathFor(game.RootDir))));

    private void Show(FrameworkElement panel)
    {
        foreach (var p in new FrameworkElement[] { SelectPanel, DetectPanel, ProgressPanel, ResultPanel })
            p.Visibility = p == panel ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ErrorMessage(string message) =>
        MessageBox.Show(this, message, "GameSubTR Patcher", MessageBoxButton.OK, MessageBoxImage.Warning);

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Error("Klasör açılamadı", ex); }
    }

    private static string EngineName(EngineKind e) => e switch
    {
        EngineKind.RenPy => "Ren'Py",
        EngineKind.RpgMakerMV => "RPG Maker MV",
        EngineKind.RpgMakerMZ => "RPG Maker MZ",
        EngineKind.Unity => "Unity",
        EngineKind.Unreal => "Unreal Engine",
        _ => "Tanınamadı"
    };

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_cts != null)
        {
            // Kurulum sürerken kapatma: önce iptal et, geri alma tamamlansın.
            e.Cancel = true;
            OnCancel(this, new RoutedEventArgs());
            return;
        }
        base.OnClosing(e);
    }
}
