using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using GameSubTR.App.Native;
using GameSubTR.Core.Imaging;
using GameSubTR.Core.Models;

namespace GameSubTR.App.UI;

/// <summary>
/// Oyunun monitörünü kaplayan, dondurulmuş ekran görüntüsü üzerinde fareyle
/// altyazı bölgesi seçtiren pencere. Exclusive fullscreen'de de çalışır
/// (önce görüntü alınır, sonra pencere açılır).
/// </summary>
public sealed class RegionSelectorWindow : Window
{
    private readonly Canvas _canvas = new();
    private readonly Rectangle _rect;
    private readonly Win32.RECT _monitor;
    private Point? _start;
    private NormalizedRect? _result;

    public NormalizedRect? Result => _result;

    public RegionSelectorWindow(Win32.RECT monitor, BgraImage? screenshot, NormalizedRect? current)
    {
        _monitor = monitor;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        AllowsTransparency = true;
        Background = screenshot == null ? new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)) : Brushes.Black;
        Cursor = Cursors.Cross;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = monitor.Left; Top = monitor.Top; Width = monitor.Width; Height = monitor.Height;
        Title = "Altyazı bölgesini seçin";

        var grid = new Grid();
        if (screenshot != null)
        {
            var bmp = BitmapSource.Create(screenshot.Width, screenshot.Height, 96, 96, PixelFormats.Bgra32, null, screenshot.Pixels, screenshot.Stride);
            grid.Children.Add(new Image { Source = bmp, Stretch = Stretch.Fill });
        }
        grid.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)) });
        grid.Children.Add(_canvas);

        var hint = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x1E, 0x1E, 0x2A)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 10, 16, 10),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 30, 0, 0),
            Child = new TextBlock
            {
                Foreground = Brushes.White, FontSize = 18, TextAlignment = TextAlignment.Center,
                Text = "İngilizce altyazının çıktığı alanı fareyle çizin (birkaç satırlık pay bırakın).\nBırakınca kaydedilir · Esc: iptal"
            }
        };
        grid.Children.Add(hint);
        Content = grid;

        _rect = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF)),
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(0x30, 0x4C, 0xC2, 0xFF)),
            Visibility = Visibility.Collapsed
        };
        _canvas.Children.Add(_rect);

        Loaded += (_, _) =>
        {
            if (current is { IsValid: true } c)
            {
                _rect.Visibility = Visibility.Visible;
                Canvas.SetLeft(_rect, c.X * ActualWidth); Canvas.SetTop(_rect, c.Y * ActualHeight);
                _rect.Width = c.Width * ActualWidth; _rect.Height = c.Height * ActualHeight;
            }
            Activate();
            Focus();
        };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, _monitor.Left, _monitor.Top, _monitor.Width, _monitor.Height, Win32.SWP_SHOWWINDOW);
        };

        MouseLeftButtonDown += (_, e) =>
        {
            _start = e.GetPosition(_canvas);
            _rect.Visibility = Visibility.Visible;
            Update(_start.Value);
            CaptureMouse();
        };
        MouseMove += (_, e) => { if (_start != null) Update(e.GetPosition(_canvas)); };
        MouseLeftButtonUp += (_, e) =>
        {
            if (_start == null) return;
            ReleaseMouseCapture();
            var end = e.GetPosition(_canvas);
            var s = _start.Value;
            _start = null;
            double x = Math.Min(s.X, end.X), y = Math.Min(s.Y, end.Y);
            double w = Math.Abs(end.X - s.X), h = Math.Abs(end.Y - s.Y);
            if (w < 20 || h < 10) return; // yanlışlıkla tıklama
            _result = new NormalizedRect(
                Math.Clamp(x / ActualWidth, 0, 1), Math.Clamp(y / ActualHeight, 0, 1),
                Math.Clamp(w / ActualWidth, 0, 1 - x / ActualWidth), Math.Clamp(h / ActualHeight, 0, 1 - y / ActualHeight));
            DialogResult = true;
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) DialogResult = false; };
    }

    private void Update(Point p)
    {
        var s = _start ?? p;
        Canvas.SetLeft(_rect, Math.Min(s.X, p.X));
        Canvas.SetTop(_rect, Math.Min(s.Y, p.Y));
        _rect.Width = Math.Abs(p.X - s.X);
        _rect.Height = Math.Abs(p.Y - s.Y);
    }
}
