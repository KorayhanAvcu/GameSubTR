using GameSubTR.App.Native;
using GameSubTR.Core.Imaging;
using GameSubTR.Core.Logging;
using GameSubTR.Core.Models;
using ScreenCapture.NET;

namespace GameSubTR.App.Capture;

/// <summary>Ekrandan (veya oyun içinden) görüntü kaynağı.</summary>
public interface IFrameSource : IDisposable
{
    string Name { get; }
    /// <summary>Normalize bölgeyi yakalar. Başarısızsa null.</summary>
    BgraImage? Capture(NormalizedRect region);
    /// <summary>Tüm ekranı yakalar (bölge seçimi için).</summary>
    BgraImage? CaptureFull();
}

/// <summary>
/// DXGI Desktop Duplication (ScreenCapture.NET.DX11). Kenarlıksız ve modern tam ekran
/// oyunlarda çalışır; GPU üzerinden yakaladığı için yükü çok düşüktür.
/// Kendi overlay penceremiz WDA_EXCLUDEFROMCAPTURE ile bu yakalamaya girmez.
/// </summary>
public sealed class DesktopFrameSource : IFrameSource
{
    private readonly object _gate = new();
    private readonly string _deviceName;
    private DX11ScreenCaptureService? _service;
    // Arayüz üzerinden kullanılır (ScreenCapture.NET 2.x ve 3.x ile uyumlu; renk tipi adı geçmez).
    private IScreenCapture? _capture;
    private ICaptureZone? _zone;
    private (int X, int Y, int W, int H) _zoneRect;
    private DateTime _lastFailure = DateTime.MinValue;

    public string Name => "DesktopDuplication";

    public DesktopFrameSource(Win32.MONITORINFOEX monitor) => _deviceName = monitor.szDevice ?? "";

    private void EnsureInit()
    {
        if (_capture != null) return;
        _service = new DX11ScreenCaptureService();
        Display? chosen = null;
        foreach (var card in _service.GetGraphicsCards())
            foreach (var d in _service.GetDisplays(card))
            {
                chosen ??= d;
                if (string.Equals(d.DeviceName, _deviceName, StringComparison.OrdinalIgnoreCase)) { chosen = d; goto found; }
            }
        found:
        if (chosen == null) throw new InvalidOperationException("Yakalanacak ekran bulunamadı");
        var dx = _service.GetScreenCapture(chosen.Value);
        dx.Timeout = 60; // ekran değişmediğinde CaptureScreen en fazla bu kadar bekler
        _capture = dx;
        Log.Info($"Ekran yakalama hazır: {chosen.Value.DeviceName} {chosen.Value.Width}x{chosen.Value.Height}");
    }

    private void Reset()
    {
        try { _capture?.Dispose(); } catch { /* yoksay */ }
        try { _service?.Dispose(); } catch { /* yoksay */ }
        _capture = null; _service = null; _zone = null;
    }

    public BgraImage? Capture(NormalizedRect region)
    {
        lock (_gate)
        {
            if (DateTime.UtcNow - _lastFailure < TimeSpan.FromSeconds(1)) return null;
            try
            {
                EnsureInit();
                var cap = _capture!;
                var px = region.ToPixels(cap.Display.Width, cap.Display.Height);
                if (_zone == null || px != _zoneRect)
                {
                    if (_zone != null) cap.UnregisterCaptureZone(_zone);
                    _zone = cap.RegisterCaptureZone(px.X, px.Y, px.W, px.H);
                    _zoneRect = px;
                }
                cap.CaptureScreen();
                using (_zone.Lock())
                {
                    if (_zone.Width <= 0 || _zone.Height <= 0) return null;
                    return BgraImage.FromBuffer(_zone.RawBuffer, _zone.Width, _zone.Height, _zone.Stride);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Ekran yakalama hatası, yeniden başlatılacak: " + ex.Message);
                _lastFailure = DateTime.UtcNow;
                Reset();
                return null;
            }
        }
    }

    public BgraImage? CaptureFull()
    {
        lock (_gate)
        {
            try
            {
                EnsureInit();
                var cap = _capture!;
                var full = cap.RegisterCaptureZone(0, 0, cap.Display.Width, cap.Display.Height);
                try
                {
                    // İlk kare boş olabilir; birkaç deneme.
                    for (int i = 0; i < 3; i++) cap.CaptureScreen();
                    using (full.Lock())
                        return BgraImage.FromBuffer(full.RawBuffer, full.Width, full.Height, full.Stride);
                }
                finally { cap.UnregisterCaptureZone(full); }
            }
            catch (Exception ex)
            {
                Log.Warn("Tam ekran yakalanamadı: " + ex.Message);
                Reset();
                return null;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate) Reset();
    }
}
