using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Text;
using GameSubTR.App.Capture;
using GameSubTR.App.Native;
using GameSubTR.Core.Imaging;
using GameSubTR.Core.Interop;
using GameSubTR.Core.Logging;
using GameSubTR.Core.Models;

namespace GameSubTR.App.Overlay;

/// <summary>
/// Gerçek exclusive fullscreen oyunlar için: hook DLL'ini oyuna yükler ve
/// paylaşımlı bellek üzerinden (1) altyazıyı oyunun kendi ekranına çizdirir,
/// (2) altyazı bölgesini oyunun arka tamponundan okur.
/// Sadece tek oyunculu / anti-cheat'siz oyunlarda kullanılmalıdır.
/// </summary>
public sealed class HookSession : ISubtitlePresenter, IFrameSource
{
    private readonly int _pid;
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;
    private readonly object _gate = new();
    private uint _textSeq;
    private uint _capSeq;
    private bool _disposed;

    public string Name => "Hook";

    private HookSession(int pid, MemoryMappedFile mmf)
    {
        _pid = pid;
        _mmf = mmf;
        _view = mmf.CreateViewAccessor(0, SharedLayout.TotalSize);
        if (_view.ReadUInt32(SharedLayout.OffMagic) != SharedLayout.Magic)
        {
            _view.Write(SharedLayout.OffVersion, SharedLayout.Version);
            _view.Write(SharedLayout.OffMagic, SharedLayout.Magic);
        }
        _textSeq = _view.ReadUInt32(SharedLayout.OffTextSeq);
        _capSeq = _view.ReadUInt32(SharedLayout.OffCaptureReqSeq);
    }

    /// <summary>Hook DLL'i son 2 saniye içinde bir kare çizdi mi?</summary>
    public bool IsAlive
    {
        get
        {
            if (_disposed) return false;
            uint hb = _view.ReadUInt32(SharedLayout.OffHeartbeat);
            uint api = _view.ReadUInt32(SharedLayout.OffHookApi);
            uint now = (uint)Environment.TickCount;
            return api != 0 && hb != 0 && now - hb < 2000;
        }
    }

    public uint Api => _view.ReadUInt32(SharedLayout.OffHookApi);

    /// <summary>Paylaşımlı belleği oluşturur, DLL'i yükler ve hook'un çalışmasını bekler.</summary>
    public static async Task<HookSession?> StartAsync(int pid, CancellationToken ct)
    {
        MemoryMappedFile mmf;
        try
        {
            mmf = MemoryMappedFile.CreateOrOpen(SharedLayout.MappingName(pid), SharedLayout.TotalSize, MemoryMappedFileAccess.ReadWrite);
        }
        catch (Exception ex)
        {
            Log.Error("Paylaşımlı bellek oluşturulamadı", ex);
            return null;
        }

        var session = new HookSession(pid, mmf);
        if (session.IsAlive)
        {
            Log.Info($"Hook zaten yüklü (pid {pid}, DX{session.Api}).");
            return session;
        }

        if (!Inject(pid, out var err))
        {
            Log.Warn("Hook yüklenemedi: " + err);
            session.Dispose();
            return null;
        }

        // DLL'in ilk kareyi çizmesini bekle.
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000)
        {
            ct.ThrowIfCancellationRequested();
            if (session.IsAlive)
            {
                Log.Info($"Hook aktif (pid {pid}, DX{session.Api}).");
                return session;
            }
            await Task.Delay(100, ct);
        }
        Log.Warn("Hook yüklendi ama desteklenen bir grafik API'si bulunamadı (DX12/Vulkan oyunlarda pencere modu kullanılır).");
        session.Dispose();
        return null;
    }

    private static bool Inject(int pid, out string error)
    {
        error = "";
        bool? is32 = Win32.Is32BitProcess((uint)pid);
        if (is32 == null) { error = "Süreç açılamadı (oyun yönetici olarak çalışıyor olabilir)."; return false; }
        string dir = AppContext.BaseDirectory;
        string suffix = is32.Value ? "32" : "64";
        string injector = Path.Combine(dir, $"GameSubTR.Injector{suffix}.exe");
        string dll = Path.Combine(dir, $"GameSubTR.Hook{suffix}.dll");
        if (!File.Exists(injector) || !File.Exists(dll))
        {
            error = $"Hook dosyaları bulunamadı: {Path.GetFileName(injector)}, {Path.GetFileName(dll)}";
            return false;
        }
        try
        {
            var psi = new ProcessStartInfo(injector)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add(pid.ToString());
            psi.ArgumentList.Add(dll);
            using var p = Process.Start(psi)!;
            if (!p.WaitForExit(10000)) { p.Kill(); error = "Injector zaman aşımı"; return false; }
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            if (p.ExitCode != 0) { error = $"Injector hata kodu {p.ExitCode}: {output.Trim()}"; return false; }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // ---------- ISubtitlePresenter ----------

    private static uint ParseArgb(string hex, double opacity)
    {
        uint rgb = 0xFFFFFF;
        try
        {
            var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
            rgb = (uint)(c.R << 16 | c.G << 8 | c.B);
        }
        catch { /* varsayılan beyaz */ }
        uint a = (uint)Math.Clamp(opacity * 255, 0, 255);
        return a << 24 | rgb;
    }

    public void Show(string text, string? original, SubtitleLayout layout, SubtitleStyle style)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var full = string.IsNullOrWhiteSpace(original) ? text : text + "\n" + original;
            var bytes = Encoding.UTF8.GetBytes(full);
            int len = Math.Min(bytes.Length, SharedLayout.TextCapacity - 1);
            _view.WriteArray(SharedLayout.OffText, bytes, 0, len);
            _view.Write(SharedLayout.OffText + len, (byte)0);
            _view.Write(SharedLayout.OffTextLen, (uint)len);
            _view.Write(SharedLayout.OffFontSize, (float)style.FontSize);
            _view.Write(SharedLayout.OffTextColor, ParseArgb(style.TextColor, 1.0));
            _view.Write(SharedLayout.OffBgColor, ParseArgb("#000000", style.BackgroundOpacity));
            _view.Write(SharedLayout.OffBoxX, (float)layout.Box.X);
            _view.Write(SharedLayout.OffBoxY, (float)layout.Box.Y);
            _view.Write(SharedLayout.OffBoxW, (float)layout.Box.Width);
            _view.Write(SharedLayout.OffBoxH, (float)layout.Box.Height);
            _view.Write(SharedLayout.OffLayoutFlags, (uint)((layout.CenterInBox ? 1 : 0) | (layout.CoverBox ? 2 : 0)));
            _view.Write(SharedLayout.OffVisible, 1u);
            Thread.MemoryBarrier();
            _view.Write(SharedLayout.OffTextSeq, ++_textSeq);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _view.Write(SharedLayout.OffVisible, 0u);
            Thread.MemoryBarrier();
            _view.Write(SharedLayout.OffTextSeq, ++_textSeq);
        }
    }

    public void ShowStatus(string message, TimeSpan duration)
    {
        // Oyun içinde kısa durum mesajı: altyazı alanını kullanır.
        Show(message, null, new SubtitleLayout(new NormalizedRect(0.02, 0.02, 0.4, 0.06), false, false), new SubtitleStyle(20, "#FFFFFF", 0.8));
        var seq = _textSeq;
        _ = Task.Delay(duration).ContinueWith(_ => { lock (_gate) if (_textSeq == seq) Clear(); });
    }

    // ---------- IFrameSource ----------

    public BgraImage? Capture(NormalizedRect region) => CaptureInternal(region, 400);

    public BgraImage? CaptureFull() => CaptureInternal(new NormalizedRect(0, 0, 1, 1), 1500);

    private BgraImage? CaptureInternal(NormalizedRect r, int timeoutMs)
    {
        uint seq;
        lock (_gate)
        {
            if (_disposed) return null;
            _view.Write(SharedLayout.OffCapX, (float)r.X);
            _view.Write(SharedLayout.OffCapY, (float)r.Y);
            _view.Write(SharedLayout.OffCapW, (float)r.Width);
            _view.Write(SharedLayout.OffCapH, (float)r.Height);
            Thread.MemoryBarrier();
            seq = ++_capSeq;
            _view.Write(SharedLayout.OffCaptureReqSeq, seq);
        }
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (_view.ReadUInt32(SharedLayout.OffCaptureDoneSeq) == seq)
            {
                Thread.MemoryBarrier();
                uint status = _view.ReadUInt32(SharedLayout.OffCapStatus);
                if (status != 0) return null;
                int w = (int)_view.ReadUInt32(SharedLayout.OffCapWidth);
                int h = (int)_view.ReadUInt32(SharedLayout.OffCapHeight);
                if (w <= 0 || h <= 0 || (long)w * h * 4 > SharedLayout.PixelCapacity) return null;
                var img = new BgraImage(w, h);
                _view.ReadArray(SharedLayout.OffPixels, img.Pixels, 0, w * h * 4);
                return img;
            }
            Thread.Sleep(2);
        }
        return null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                _view.Write(SharedLayout.OffVisible, 0u);
                _view.Write(SharedLayout.OffTextSeq, ++_textSeq);
            }
            catch { /* yoksay */ }
            _disposed = true;
            _view.Dispose();
            _mmf.Dispose();
        }
    }
}
