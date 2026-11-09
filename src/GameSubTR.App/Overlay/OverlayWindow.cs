using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using GameSubTR.App.Native;
using GameSubTR.Core.Logging;

namespace GameSubTR.App.Overlay;

/// <summary>
/// Şeffaf, her zaman üstte, tıklamaları alttaki oyuna geçiren altyazı penceresi.
/// Kendisi ekran yakalamaya dahil edilmez (WDA_EXCLUDEFROMCAPTURE), böylece
/// orijinal altyazının üstüne çizilse bile OCR orijinali okumaya devam eder.
/// </summary>
public sealed class OverlayWindow : Window
{
    private readonly Canvas _canvas = new();
    private readonly Border _box = new();
    private readonly TextBlock _text = new();
    private readonly TextBlock _original = new();
    private readonly Border _statusBox = new();
    private readonly TextBlock _status = new();
    private readonly DispatcherTimer _topmostTimer;
    private readonly DispatcherTimer _statusTimer;
    private IntPtr _hwnd;
    private Win32.RECT _monitor;

    public OverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = "GameSubTR Overlay";
        Left = -10000; Top = -10000; Width = 100; Height = 100;

        _text.TextWrapping = TextWrapping.Wrap;
        _text.TextAlignment = TextAlignment.Center;
        _text.FontWeight = FontWeights.SemiBold;
        _text.FontFamily = new FontFamily("Segoe UI");
        _text.Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 6, ShadowDepth = 0, Opacity = 1 };

        _original.TextWrapping = TextWrapping.Wrap;
        _original.TextAlignment = TextAlignment.Center;
        _original.FontFamily = new FontFamily("Segoe UI");
        _original.Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8));
        _original.Margin = new Thickness(0, 4, 0, 0);
        _original.Visibility = Visibility.Collapsed;

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(_text);
        stack.Children.Add(_original);
        _box.Child = stack;
        _box.CornerRadius = new CornerRadius(6);
        _box.Padding = new Thickness(14, 6, 14, 8);
        _box.Visibility = Visibility.Collapsed;

        _status.Foreground = Brushes.White;
        _status.FontSize = 16;
        _status.FontFamily = new FontFamily("Segoe UI");
        _statusBox.Child = _status;
        _statusBox.Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x1E, 0x1E, 0x2A));
        _statusBox.CornerRadius = new CornerRadius(6);
        _statusBox.Padding = new Thickness(12, 6, 12, 6);
        _statusBox.Visibility = Visibility.Collapsed;
        Canvas.SetLeft(_statusBox, 24); Canvas.SetTop(_statusBox, 24);

        _canvas.Children.Add(_box);
        _canvas.Children.Add(_statusBox);
        Content = _canvas;

        _topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        _topmostTimer.Tick += (_, _) => ReassertTopmost();
        _statusTimer = new DispatcherTimer();
        _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); _statusBox.Visibility = Visibility.Collapsed; UpdateVisibility(); };

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Win32.MakeClickThrough(_hwnd);
            if (!Win32.SetWindowDisplayAffinity(_hwnd, Win32.WDA_EXCLUDEFROMCAPTURE))
                Log.Warn("WDA_EXCLUDEFROMCAPTURE desteklenmiyor (Windows 10 2004+ gerekir). 'Orijinalin üstüne' modunda OCR kendi çevirimizi okuyabilir.");
            ApplyBounds();
            UpdateVisibility();
        };
        _topmostTimer.Start();
    }

    /// <summary>Pencereyi oyunun monitörünü kaplayacak şekilde konumlandırır (fiziksel piksel).</summary>
    public void SetMonitor(Win32.RECT monitor)
    {
        _monitor = monitor;
        ApplyBounds();
    }

    private void ApplyBounds()
    {
        if (_hwnd == IntPtr.Zero || _monitor.Width <= 0) return;
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, _monitor.Left, _monitor.Top, _monitor.Width, _monitor.Height,
            Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);
    }

    private bool _shown = true;

    /// <summary>
    /// Gösterilecek bir şey yokken pencereyi tamamen gizler; böylece oyunun
    /// "independent flip" modu bozulmaz ve FPS/gecikme etkilenmez.
    /// </summary>
    private void UpdateVisibility()
    {
        if (_hwnd == IntPtr.Zero) return;
        bool want = _box.Visibility == Visibility.Visible || _statusBox.Visibility == Visibility.Visible;
        if (want == _shown) return;
        _shown = want;
        Win32.ShowWindow(_hwnd, want ? Win32.SW_SHOWNOACTIVATE : Win32.SW_HIDE);
        if (want) ReassertTopmost();
    }

    private void ReassertTopmost()
    {
        if (_hwnd != IntPtr.Zero && (_box.Visibility == Visibility.Visible || _statusBox.Visibility == Visibility.Visible))
            Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);
    }

    private static SolidColorBrush ParseBrush(string hex, SolidColorBrush fallback)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        catch { return fallback; }
    }

    public void ShowSubtitle(string text, string? original, SubtitleLayout layout, SubtitleStyle style)
    {
        double w = ActualWidth > 0 ? ActualWidth : _monitor.Width;
        double h = ActualHeight > 0 ? ActualHeight : _monitor.Height;
        if (w <= 0 || h <= 0) return;
        double scale = h / 1080.0; // yazı boyutu 1080p'ye göre tanımlı

        _text.Text = text;
        _text.FontSize = Math.Max(10, style.FontSize * scale);
        _text.Foreground = ParseBrush(style.TextColor, Brushes.White);
        _original.Text = original ?? "";
        _original.FontSize = Math.Max(9, style.FontSize * 0.6 * scale);
        _original.Visibility = string.IsNullOrWhiteSpace(original) ? Visibility.Collapsed : Visibility.Visible;
        _box.Background = new SolidColorBrush(Color.FromArgb((byte)Math.Clamp(style.BackgroundOpacity * 255, 0, 255), 0, 0, 0));

        var b = layout.Box;
        double boxX = b.X * w, boxY = b.Y * h, boxW = b.Width * w, boxH = b.Height * h;
        double maxW = Math.Max(boxW, w * 0.45);
        maxW = Math.Min(maxW, w * 0.96);
        _box.MaxWidth = maxW;
        _box.MinWidth = layout.CoverBox ? Math.Min(boxW, maxW) : 0;
        _box.MinHeight = layout.CoverBox ? boxH : 0;
        _box.Visibility = Visibility.Visible;
        _box.Measure(new Size(maxW, double.PositiveInfinity));
        var size = _box.DesiredSize;

        double left = boxX + boxW / 2 - size.Width / 2;
        double top = layout.CenterInBox ? boxY + boxH / 2 - size.Height / 2 : boxY + boxH - size.Height;
        left = Math.Clamp(left, 0, Math.Max(0, w - size.Width));
        top = Math.Clamp(top, 0, Math.Max(0, h - size.Height));
        Canvas.SetLeft(_box, left);
        Canvas.SetTop(_box, top);
        UpdateVisibility();
        ReassertTopmost();
    }

    public void ClearSubtitle()
    {
        _box.Visibility = Visibility.Collapsed;
        UpdateVisibility();
    }

    public void ShowStatusMessage(string message, TimeSpan duration)
    {
        _status.Text = message;
        _statusBox.Visibility = Visibility.Visible;
        _statusTimer.Stop();
        _statusTimer.Interval = duration;
        _statusTimer.Start();
        UpdateVisibility();
        ReassertTopmost();
    }
}

/// <summary>OverlayWindow'u UI iş parçacığından güvenle kullanan sunucu.</summary>
public sealed class WindowPresenter : ISubtitlePresenter
{
    private readonly Dispatcher _ui;
    private OverlayWindow? _window;

    public string Name => "Pencere";
    public bool IsAlive => _window != null;

    public WindowPresenter(Dispatcher ui, Win32.RECT monitor)
    {
        _ui = ui;
        _ui.Invoke(() =>
        {
            _window = new OverlayWindow();
            _window.Show();
            _window.SetMonitor(monitor);
        });
    }

    public void SetMonitor(Win32.RECT monitor) => _ui.BeginInvoke(() => _window?.SetMonitor(monitor));

    public void Show(string text, string? original, SubtitleLayout layout, SubtitleStyle style) =>
        _ui.BeginInvoke(() => _window?.ShowSubtitle(text, original, layout, style));

    public void Clear() => _ui.BeginInvoke(() => _window?.ClearSubtitle());

    public void ShowStatus(string message, TimeSpan duration) => _ui.BeginInvoke(() => _window?.ShowStatusMessage(message, duration));

    public void Dispose() => _ui.Invoke(() => { _window?.Close(); _window = null; });
}
