using GameSubTR.App.Native;
using GameSubTR.Core.Models;

namespace GameSubTR.App.Services;

/// <summary>Ön plandaki oyun hakkında bilgi.</summary>
public sealed record GameInfo(int Pid, string ExeName, IntPtr Hwnd, Win32.RECT Monitor, bool IsFullscreenWindow, bool IsExclusive)
{
    public int MonitorWidth => Monitor.Width;
    public int MonitorHeight => Monitor.Height;
}

/// <summary>
/// Ön plan penceresini izler. Bir program "oyun" sayılır: profili varsa ya da
/// penceresi bulunduğu monitörü tamamen kaplıyorsa (tam ekran / kenarlıksız).
/// </summary>
public sealed class ForegroundWatcher : IDisposable
{
    private readonly Func<AppSettings> _settings;
    private readonly System.Threading.Timer _timer;
    private readonly int _ownPid = Environment.ProcessId;
    private GameInfo? _current;
    private int _busy;

    public event Action<GameInfo?>? GameChanged;
    public GameInfo? Current => _current;

    public ForegroundWatcher(Func<AppSettings> settings)
    {
        _settings = settings;
        _timer = new System.Threading.Timer(_ => Tick(), null, 500, 400);
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var info = Detect();
            var prev = _current;
            bool changed = (prev == null) != (info == null) ||
                           (prev != null && info != null && (prev.Pid != info.Pid || prev.Hwnd != info.Hwnd ||
                            prev.Monitor.Left != info.Monitor.Left || prev.Monitor.Width != info.Monitor.Width ||
                            prev.Monitor.Height != info.Monitor.Height || prev.IsExclusive != info.IsExclusive));
            _current = info;
            if (changed) GameChanged?.Invoke(info);
        }
        catch (Exception ex) { Core.Logging.Log.Error("ForegroundWatcher", ex); }
        finally { Volatile.Write(ref _busy, 0); }
    }

    private GameInfo? Detect()
    {
        var hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero || Win32.IsIconic(hwnd)) return null;
        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || pid == _ownPid) return _current; // kendi pencerelerimiz (ör. bölge seçici) mevcut oyunu bozmaz
        var path = Win32.GetProcessPath(pid);
        if (path == null) return null;
        var exe = Path.GetFileName(path).ToLowerInvariant();
        var s = _settings();
        if (s.IgnoredExes.Contains(exe, StringComparer.OrdinalIgnoreCase)) return null;

        var mi = Win32.GetMonitorForWindow(hwnd);
        Win32.GetWindowRect(hwnd, out var wr);
        bool covers = wr.Left <= mi.rcMonitor.Left && wr.Top <= mi.rcMonitor.Top &&
                      wr.Right >= mi.rcMonitor.Right && wr.Bottom >= mi.rcMonitor.Bottom;
        bool hasProfile = s.Profiles.ContainsKey(exe);
        if (!covers && !hasProfile) return null;

        return new GameInfo((int)pid, exe, hwnd, mi.rcMonitor, covers, covers && Win32.IsExclusiveFullscreen());
    }

    /// <summary>Profil oluşturulduktan sonra hemen yeniden değerlendir.</summary>
    public void Refresh() => Tick();

    public void Dispose() => _timer.Dispose();
}
