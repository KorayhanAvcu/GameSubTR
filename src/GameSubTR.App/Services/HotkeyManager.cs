using System.Windows.Interop;
using GameSubTR.App.Native;
using GameSubTR.Core.Logging;

namespace GameSubTR.App.Services;

/// <summary>Sistem genelinde kısayol tuşları (tam ekran oyunlarda da çalışır).</summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 0xB000;

    public HotkeyManager()
    {
        var p = new HwndSourceParameters("GameSubTR.Hotkeys") { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */ };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    public bool Register(string gesture, Action action, out string? error)
    {
        error = null;
        if (!TryParse(gesture, out var mods, out var vk))
        {
            error = $"Kısayol anlaşılamadı: {gesture}";
            return false;
        }
        int id = _nextId++;
        if (!Win32.RegisterHotKey(_source.Handle, id, mods | Win32.MOD_NOREPEAT, vk))
        {
            error = $"Kısayol başka bir program tarafından kullanılıyor: {gesture}";
            Log.Warn(error);
            return false;
        }
        _actions[id] = action;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys) Win32.UnregisterHotKey(_source.Handle, id);
        _actions.Clear();
    }

    public static bool TryParse(string gesture, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        if (string.IsNullOrWhiteSpace(gesture)) return false;
        foreach (var raw in gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= Win32.MOD_CONTROL; break;
                case "shift": mods |= Win32.MOD_SHIFT; break;
                case "alt": mods |= Win32.MOD_ALT; break;
                case "win": mods |= Win32.MOD_WIN; break;
                default:
                    if (Enum.TryParse<System.Windows.Input.Key>(raw, true, out var key))
                        vk = (uint)System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);
                    else return false;
                    break;
            }
        }
        return vk != 0;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var a))
        {
            handled = true;
            try { a(); } catch (Exception ex) { Log.Error("Kısayol işleyicisi", ex); }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.Dispose();
    }
}
