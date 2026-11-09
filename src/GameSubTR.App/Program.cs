using System.Windows;
using GameSubTR.Core.Logging;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace GameSubTR.App;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        using var mutex = new Mutex(true, "Local\\GameSubTR.SingleInstance", out bool first);
        if (!first)
        {
            MessageBox.Show("GameSubTR zaten çalışıyor (sistem tepsisine bakın).", "GameSubTR");
            return;
        }

        Log.Init(Path.Combine(SubtitleController.DataDir, "logs"));
        Log.Info($"GameSubTR başlıyor. Windows {Environment.OSVersion.Version}");

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Error("Yakalanmamış UI hatası", e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Yakalanmamış hata", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("Gözlenmeyen görev hatası", e.Exception); e.SetObserved(); };

        app.Startup += (_, _) =>
        {
            var controller = new SubtitleController(app.Dispatcher);
            var tray = new TrayIcon(controller, () =>
            {
                controller.Dispose();
                app.Shutdown();
            });
            controller.StatusChanged += m => app.Dispatcher.BeginInvoke(() => tray.SetStatus(m));
            if (!File.Exists(Path.Combine(SubtitleController.DataDir, "settings.json")))
                tray.ShowBalloon("GameSubTR çalışıyor",
                    $"Oyunu açın, {controller.Settings.Hotkeys.SelectRegion} ile altyazı bölgesini seçin. Ayarlar için tepsi simgesine sağ tıklayın.");

            // Yeni sürüm kontrolü (GitHub Releases).
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(8));
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("GameSubTR/" + Services.UpdateChecker.CurrentVersion);
                var tag = await Services.UpdateChecker.CheckAsync(http);
                if (tag != null)
                    app.Dispatcher.Invoke(() => tray.ShowUpdate(tag));
            });
        };
        app.Run();
    }
}

/// <summary>Sistem tepsisi simgesi ve menüsü.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly SubtitleController _c;
    private readonly Forms.ToolStripMenuItem _enabled, _modeOcr, _modeAudio, _modeBoth, _gameLabel;

    public TrayIcon(SubtitleController controller, Action exit)
    {
        _c = controller;
        _icon = new Forms.NotifyIcon { Icon = CreateIcon(), Text = "GameSubTR", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        _gameLabel = new Forms.ToolStripMenuItem("Oyun: —") { Enabled = false };
        _enabled = new Forms.ToolStripMenuItem("Çeviri açık", null, (_, _) => _c.ToggleEnabled());
        var region = new Forms.ToolStripMenuItem("Altyazı bölgesini seç", null, (_, _) => _ = _c.SelectRegionAsync());
        var mode = new Forms.ToolStripMenuItem("Kaynak");
        _modeOcr = new Forms.ToolStripMenuItem("Ekran altyazısı (OCR)", null, (_, _) => SetMode(Core.Models.SourceMode.Ocr));
        _modeAudio = new Forms.ToolStripMenuItem("Oyun sesi (Whisper)", null, (_, _) => SetMode(Core.Models.SourceMode.Audio));
        _modeBoth = new Forms.ToolStripMenuItem("İkisi birden", null, (_, _) => SetMode(Core.Models.SourceMode.Both));
        mode.DropDownItems.AddRange(new Forms.ToolStripItem[] { _modeOcr, _modeAudio, _modeBoth });
        var settings = new Forms.ToolStripMenuItem("Ayarlar...", null, (_, _) => _c.OpenSettings());
        var about = new Forms.ToolStripMenuItem($"Sürüm {Services.UpdateChecker.CurrentVersion.ToString(3)} — sürümler sayfası", null, (_, _) => OpenReleases());
        var logs = new Forms.ToolStripMenuItem("Log klasörünü aç", null, (_, _) =>
            System.Diagnostics.Process.Start("explorer.exe", Path.Combine(SubtitleController.DataDir, "logs")));
        var quit = new Forms.ToolStripMenuItem("Çıkış", null, (_, _) => { Dispose(); exit(); });
        menu.Items.AddRange(new Forms.ToolStripItem[] { _gameLabel, new Forms.ToolStripSeparator(), _enabled, region, mode, settings, logs, about, new Forms.ToolStripSeparator(), quit });
        menu.Opening += (_, _) => RefreshMenu();
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => _c.OpenSettings();
        _icon.BalloonTipClicked += (_, _) => { if (_updateTag != null) OpenReleases(); };
    }

    private string? _updateTag;

    public void ShowUpdate(string tag)
    {
        _updateTag = tag;
        _icon.ShowBalloonTip(10000, "GameSubTR güncellemesi var",
            $"Yeni sürüm {tag} yayınlandı (sizdeki: v{Services.UpdateChecker.CurrentVersion.ToString(3)}). İndirmek için tıklayın.",
            Forms.ToolTipIcon.Info);
    }

    private static void OpenReleases() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Services.UpdateChecker.ReleasesUrl) { UseShellExecute = true });

    private void SetMode(Core.Models.SourceMode m)
    {
        var g = _c.CurrentGame;
        if (g == null) { ShowBalloon("GameSubTR", "Kaynak modu oyun bazlıdır; önce oyunu açın."); return; }
        _c.SetMode(m);
    }

    private void RefreshMenu()
    {
        _enabled.Checked = _c.Settings.Enabled;
        var g = _c.CurrentGame;
        _gameLabel.Text = g == null ? "Oyun: algılanmadı" : $"Oyun: {g.ExeName}";
        var m = g == null ? (Core.Models.SourceMode?)null : _c.Settings.GetOrCreateProfile(g.ExeName).Mode;
        _modeOcr.Checked = m == Core.Models.SourceMode.Ocr;
        _modeAudio.Checked = m == Core.Models.SourceMode.Audio;
        _modeBoth.Checked = m == Core.Models.SourceMode.Both;
    }

    public void SetStatus(string s)
    {
        var t = "GameSubTR - " + s.Replace('\n', ' ');
        _icon.Text = t.Length > 63 ? t[..63] : t;
    }

    public void ShowBalloon(string title, string text)
    {
        _updateTag = null;
        _icon.ShowBalloonTip(6000, title, text, Forms.ToolTipIcon.Info);
    }

    private static Drawing.Icon CreateIcon()
    {
        using var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            using var bg = new Drawing.SolidBrush(Drawing.Color.FromArgb(0x1E, 0x88, 0xE5));
            g.FillEllipse(bg, 1, 1, 30, 30);
            using var f = new Drawing.Font("Segoe UI", 11, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel);
            var sf = new Drawing.StringFormat { Alignment = Drawing.StringAlignment.Center, LineAlignment = Drawing.StringAlignment.Center };
            g.DrawString("TR", f, Drawing.Brushes.White, new Drawing.RectangleF(0, 0, 32, 32), sf);
        }
        return Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
