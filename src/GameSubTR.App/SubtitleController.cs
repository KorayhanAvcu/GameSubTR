using System.Net.Http;
using System.Windows.Threading;
using GameSubTR.App.Audio;
using GameSubTR.App.Capture;
using GameSubTR.App.Native;
using GameSubTR.App.Ocr;
using GameSubTR.App.Overlay;
using GameSubTR.App.Services;
using GameSubTR.App.UI;
using GameSubTR.Core.Logging;
using GameSubTR.Core.Models;
using GameSubTR.Core.Translation;

namespace GameSubTR.App;

/// <summary>
/// Tüm parçaları yöneten merkez: oyun algılama → kaynak (OCR/Ses) → çeviri → gösterim.
/// </summary>
public sealed class SubtitleController : IDisposable
{
    public static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameSubTR");

    private readonly Dispatcher _ui;
    private readonly SettingsStore _store;
    private AppSettings _settings;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(30) };
    private readonly TranslationCache _cache;
    private readonly TranslationService _translator;
    private readonly ForegroundWatcher _watcher;
    private readonly HotkeyManager _hotkeys;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly WindowsOcr? _ocr;
    private readonly string? _ocrError;

    private GameInfo? _game;
    private ISubtitlePresenter? _presenter;
    private WindowPresenter? _windowPresenter;
    private HookSession? _hook;
    private IFrameSource? _frameSource;
    private DesktopFrameSource? _desktopSource;
    private OcrPipeline? _ocrPipeline;
    private AudioPipeline? _audioPipeline;
    private Task<WhisperTranscriber?>? _whisperTask;
    private string? _whisperModelLoaded;
    private CancellationTokenSource _sessionCts = new();

    private long _seq;
    private DateTime _lastOcrText = DateTime.MinValue;
    private string _lastSource = "";
    private string _lastShownText = "";
    private string? _lastShownOriginal;
    private string _lastShownOrigin = "";

    public event Action<string>? StatusChanged;
    public AppSettings Settings => _settings;
    public GameInfo? CurrentGame => _game;

    public SubtitleController(Dispatcher ui)
    {
        _ui = ui;
        _store = new SettingsStore(Path.Combine(DataDir, "settings.json"));
        _settings = _store.Load();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("GameSubTR/1.0");
        _cache = new TranslationCache(Path.Combine(DataDir, "cache"));
        _translator = CreateTranslationService(() => _settings, _cache);

        _ocr = WindowsOcr.TryCreate(out _ocrError);
        if (_ocr == null) Log.Warn(_ocrError!);
        else Log.Info("OCR dili: " + _ocr.Language);

        _hotkeys = new HotkeyManager();
        RegisterHotkeys();

        _watcher = new ForegroundWatcher(() => _settings);
        _watcher.GameChanged += g => _ = OnGameChangedAsync(g);
    }

    private TranslationService CreateTranslationService(Func<AppSettings> s, TranslationCache? cache)
    {
        var list = new ITranslator[]
        {
            new ClaudeTranslator(_http, () => s().Translation.AnthropicApiKey, () => s().Translation.AnthropicModel),
            new OpenAiCompatibleTranslator(_http, () => s().Translation.OpenAiBaseUrl, () => s().Translation.OpenAiApiKey, () => s().Translation.OpenAiModel),
            new DeepLTranslator(_http, () => s().Translation.DeepLApiKey),
            new GoogleFreeTranslator(_http, () => s().Translation.UseGoogleFree)
        };
        return new TranslationService(list, () => s().Translation.Order, () => s().Translation.ContextLines, cache);
    }

    // ---------------------------------------------------------------- kısayollar

    private void RegisterHotkeys()
    {
        _hotkeys.UnregisterAll();
        var h = _settings.Hotkeys;
        var errors = new List<string>();
        void Reg(string g, Action a) { if (!_hotkeys.Register(g, a, out var e) && e != null) errors.Add(e); }
        Reg(h.Toggle, ToggleEnabled);
        Reg(h.SelectRegion, () => _ = SelectRegionAsync());
        Reg(h.CycleMode, CycleMode);
        Reg(h.ToggleOriginal, ToggleOriginal);
        if (errors.Count > 0) Status(string.Join("\n", errors));
    }

    public void ToggleEnabled()
    {
        _settings.Enabled = !_settings.Enabled;
        Save();
        if (_settings.Enabled)
        {
            Status("Çeviri AÇIK");
            _ = OnGameChangedAsync(_watcher.Current, force: true);
        }
        else
        {
            _ = StopSessionAsync(keepPresenter: true).ContinueWith(_ => Status("Çeviri KAPALI"));
        }
    }

    public void CycleMode()
    {
        var g = _game; if (g == null) { Status("Önce oyunu açın."); return; }
        var p = _settings.GetOrCreateProfile(g.ExeName);
        SetMode(p.Mode switch { SourceMode.Ocr => SourceMode.Audio, SourceMode.Audio => SourceMode.Both, _ => SourceMode.Ocr });
    }

    public void SetMode(SourceMode mode)
    {
        var g = _game; if (g == null) { Status("Önce oyunu açın."); return; }
        var p = _settings.GetOrCreateProfile(g.ExeName);
        p.Mode = mode;
        Save();
        Status("Mod: " + ModeName(p.Mode));
        _ = OnGameChangedAsync(g, force: true);
    }

    public void ToggleOriginal()
    {
        var g = _game; if (g == null) return;
        var p = _settings.GetOrCreateProfile(g.ExeName);
        p.ShowOriginal = !p.ShowOriginal;
        Save();
        Status(p.ShowOriginal ? "Orijinal metin gösteriliyor" : "Orijinal metin gizlendi");
        if (_lastShownText.Length > 0) Present(_lastShownText, _lastShownOriginal, _lastShownOrigin);
    }

    public static string ModeName(SourceMode m) => m switch
    {
        SourceMode.Ocr => "Ekran altyazısı (OCR)",
        SourceMode.Audio => "Oyun sesi (Whisper)",
        _ => "OCR + Ses"
    };

    // ---------------------------------------------------------------- oturum

    private async Task OnGameChangedAsync(GameInfo? game, bool force = false)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!force && SameGame(_game, game)) return;
            var prev = _game;
            _game = game;

            if (game == null || !_settings.Enabled)
            {
                // Oyundan çıkıldı (alt-tab vb.): yakalamayı durdur, gösterimi gizle.
                await StopPipelinesAsync().ConfigureAwait(false);
                _presenter?.Clear();
                return;
            }

            bool newProcess = prev == null || prev.Pid != game.Pid;
            await StopPipelinesAsync().ConfigureAwait(false);
            if (newProcess) DisposePresenters();
            _sessionCts = new CancellationTokenSource();
            await StartSessionAsync(game, _sessionCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("Oturum başlatılamadı", ex);
            Status("Hata: " + ex.Message);
        }
        finally { _gate.Release(); }
    }

    private static bool SameGame(GameInfo? a, GameInfo? b) =>
        (a == null && b == null) || (a != null && b != null && a.Pid == b.Pid && a.Hwnd == b.Hwnd &&
                                     a.Monitor.Left == b.Monitor.Left && a.Monitor.Width == b.Monitor.Width &&
                                     a.Monitor.Height == b.Monitor.Height && a.IsExclusive == b.IsExclusive);

    private async Task StartSessionAsync(GameInfo game, CancellationToken ct)
    {
        var profile = _settings.GetOrCreateProfile(game.ExeName);
        Log.Info($"Oyun: {game.ExeName} (pid {game.Pid}) {game.MonitorWidth}x{game.MonitorHeight} exclusive={game.IsExclusive} mod={profile.Mode} gösterim={profile.Presenter}");

        // 1) Gösterim katmanı
        bool wantHook = profile.Presenter == PresenterMode.Hook || (profile.Presenter == PresenterMode.Auto && game.IsExclusive);
        if (wantHook && (_hook == null || !_hook.IsAlive))
        {
            _hook?.Dispose();
            _hook = await HookSession.StartAsync(game.Pid, ct).ConfigureAwait(false);
            if (_hook == null) Status("Oyun içi gösterim kurulamadı, pencere modu kullanılıyor.");
        }
        if (wantHook && _hook != null)
        {
            _presenter = _hook;
        }
        else
        {
            _windowPresenter ??= new WindowPresenter(_ui, game.Monitor);
            _windowPresenter.SetMonitor(game.Monitor);
            _presenter = _windowPresenter;
        }

        // 2) Görüntü kaynağı: hook varsa oyunun kendi tamponu (exclusive'de en güvenilir), yoksa masaüstü kopyalama.
        if (_hook != null && _hook.IsAlive && wantHook)
            _frameSource = _hook;
        else
        {
            _desktopSource?.Dispose();
            _desktopSource = new DesktopFrameSource(Win32.GetMonitorForWindow(game.Hwnd));
            _frameSource = _desktopSource;
        }

        // 3) Kaynaklar
        if (profile.Mode is SourceMode.Ocr or SourceMode.Both)
        {
            if (_ocr == null) Status(_ocrError ?? "OCR kullanılamıyor");
            else
            {
                _ocrPipeline = new OcrPipeline(_ocr, () => _frameSource, () => _settings.GetOrCreateProfile(game.ExeName));
                _ocrPipeline.TextReady += t => OnSourceText(t, "ocr", game);
                _ocrPipeline.TextCleared += () => OnOcrCleared();
                _ocrPipeline.Start();
                if (profile.Region is not { IsValid: true } && _announcedPid != game.Pid)
                    Status($"Altyazı bölgesini seçmek için {_settings.Hotkeys.SelectRegion} tuşlarına basın", 6);
            }
        }
        if (profile.Mode is SourceMode.Audio or SourceMode.Both)
        {
            var whisper = await EnsureWhisperAsync(ct).ConfigureAwait(false);
            if (whisper != null && !ct.IsCancellationRequested)
            {
                try
                {
                    _audioPipeline = new AudioPipeline(game.Pid, _settings.Audio, whisper);
                    _audioPipeline.TextReady += t => OnSourceText(t, "audio", game);
                    _audioPipeline.Error += e => Status(e);
                }
                catch (Exception ex)
                {
                    Log.Error("Ses hattı başlatılamadı", ex);
                    Status("Ses modu başlatılamadı: " + ex.Message);
                }
            }
        }
        if (_announcedPid != game.Pid)
        {
            _announcedPid = game.Pid;
            Status($"{profile.DisplayName}: {ModeName(profile.Mode)} · {_presenter?.Name}", 3);
        }
    }

    private int _announcedPid;

    private Task<WhisperTranscriber?> EnsureWhisperAsync(CancellationToken ct)
    {
        var model = _settings.Audio.WhisperModel;
        if (_whisperTask != null && _whisperModelLoaded == model) return _whisperTask;
        var old = _whisperTask;
        _whisperModelLoaded = model;
        _whisperTask = Task.Run(async () =>
        {
            try
            {
                if (old != null) (await old.ConfigureAwait(false))?.Dispose();
                var progress = new Progress<double>(p => Status($"Whisper modeli indiriliyor: %{p * 100:0}", 2));
                var path = await ModelManager.EnsureWhisperAsync(model, _http, progress, CancellationToken.None).ConfigureAwait(false);
                Status("Ses modeli yükleniyor...", 3);
                return WhisperTranscriber.Load(path);
            }
            catch (Exception ex)
            {
                Log.Error("Whisper hazırlanamadı", ex);
                Status("Ses modeli yüklenemedi: " + ex.Message, 8);
                _whisperModelLoaded = null;
                return null;
            }
        });
        return _whisperTask;
    }

    private async Task StopPipelinesAsync()
    {
        _sessionCts.Cancel();
        var ocr = _ocrPipeline; var audio = _audioPipeline;
        _ocrPipeline = null; _audioPipeline = null;
        await Task.Run(() => { ocr?.Dispose(); audio?.Dispose(); }).ConfigureAwait(false);
        Interlocked.Increment(ref _seq);
    }

    private async Task StopSessionAsync(bool keepPresenter)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopPipelinesAsync().ConfigureAwait(false);
            _presenter?.Clear();
            if (!keepPresenter) DisposePresenters();
        }
        finally { _gate.Release(); }
    }

    private void DisposePresenters()
    {
        _presenter = null;
        _frameSource = null;
        _desktopSource?.Dispose(); _desktopSource = null;
        _hook?.Dispose(); _hook = null;
    }

    // ---------------------------------------------------------------- metin → çeviri → gösterim

    private void OnSourceText(string text, string origin, GameInfo game)
    {
        if (origin == "ocr") _lastOcrText = DateTime.UtcNow;
        else if (DateTime.UtcNow - _lastOcrText < TimeSpan.FromSeconds(2.5))
            return; // ekranda altyazı varken sesi tekrar çevirme

        if (string.Equals(text, _lastSource, StringComparison.OrdinalIgnoreCase)) return;
        _lastSource = text;
        long seq = Interlocked.Increment(ref _seq);
        var profile = _settings.GetOrCreateProfile(game.ExeName);
        _ = Task.Run(async () =>
        {
            try
            {
                var r = await _translator.TranslateAsync(text, game.ExeName, profile.Glossary, _sessionCts.Token).ConfigureAwait(false);
                if (Interlocked.Read(ref _seq) != seq) return; // daha yeni bir altyazı geldi
                Log.Info($"[{r.Engine} {r.Elapsed.TotalMilliseconds:0}ms] {text}  =>  {r.Text}");
                Present(r.Text, text, origin);
                if (origin == "audio")
                {
                    var ms = Math.Clamp(1500 + r.Text.Length * 65, 2500, 9000);
                    await Task.Delay(ms).ConfigureAwait(false);
                    if (Interlocked.Read(ref _seq) == seq) { _presenter?.Clear(); _lastShownText = ""; _lastSource = ""; }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Error("Çeviri", ex);
                Status("Çeviri hatası: " + ex.Message, 5);
            }
        });
    }

    private void Present(string translated, string original, string origin)
    {
        var g = _game; if (g == null) return;
        var profile = _settings.GetOrCreateProfile(g.ExeName);
        _lastShownText = translated; _lastShownOriginal = original; _lastShownOrigin = origin;
        var layout = origin == "audio" && profile.Placement == OverlayPlacement.OverOriginal
            ? LayoutCalculator.For(new GameProfile { Placement = OverlayPlacement.Fixed, FixedBottom = profile.FixedBottom })
            : LayoutCalculator.For(profile);
        _presenter?.Show(translated, profile.ShowOriginal ? original : null, layout, SubtitleStyle.From(profile));
    }

    private void OnOcrCleared()
    {
        Interlocked.Increment(ref _seq);
        _lastSource = "";
        if (_lastShownOrigin == "ocr") { _presenter?.Clear(); _lastShownText = ""; }
    }

    // ---------------------------------------------------------------- bölge seçimi

    public async Task SelectRegionAsync()
    {
        var game = _game ?? _watcher.Current;
        if (game == null)
        {
            Status("Bölge seçmek için önce oyuna geçin.");
            return;
        }
        var profile = _settings.GetOrCreateProfile(game.ExeName);
        var source = _frameSource ?? (IFrameSource)new DesktopFrameSource(Win32.GetMonitorForWindow(game.Hwnd));
        var shot = await Task.Run(() => source.CaptureFull()).ConfigureAwait(false);
        if (!ReferenceEquals(source, _frameSource)) source.Dispose();

        NormalizedRect? result = null;
        await _ui.InvokeAsync(() =>
        {
            var w = new RegionSelectorWindow(game.Monitor, shot, profile.Region);
            if (w.ShowDialog() == true) result = w.Result;
        });
        Win32.SetForegroundWindow(game.Hwnd);

        if (result != null)
        {
            profile.Region = result;
            if (profile.Mode == SourceMode.Audio) profile.Mode = SourceMode.Both;
            Save();
            _ocrPipeline?.Reset();
            Status("Altyazı bölgesi kaydedildi", 3);
            if (_ocrPipeline == null) _ = OnGameChangedAsync(game, force: true);
        }
    }

    // ---------------------------------------------------------------- ayarlar

    public void OpenSettings()
    {
        _ui.Invoke(() =>
        {
            var w = new SettingsWindow(_settings, game => _cache.Clear(game), TestTranslateAsync);
            if (w.ShowDialog() == true && w.Saved != null)
            {
                _settings = w.Saved;
                Save();
                RegisterHotkeys();
                _watcher.Refresh();
                _ = OnGameChangedAsync(_watcher.Current, force: true);
            }
        });
    }

    private async Task<string> TestTranslateAsync(AppSettings s, string text)
    {
        var svc = CreateTranslationService(() => s, null);
        var r = await svc.TranslateAsync(text, "test", new Dictionary<string, string>(), CancellationToken.None);
        return $"{r.Text}\n\n(motor: {r.Engine}, {r.Elapsed.TotalMilliseconds:0} ms)";
    }

    private void Save()
    {
        try { _store.Save(_settings); }
        catch (Exception ex) { Log.Error("Ayarlar kaydedilemedi", ex); }
    }

    public void Status(string message, double seconds = 4)
    {
        Log.Info("Durum: " + message);
        StatusChanged?.Invoke(message);
        _presenter?.ShowStatus(message, TimeSpan.FromSeconds(seconds));
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _hotkeys.Dispose();
        StopSessionAsync(keepPresenter: false).Wait(3000);
        _windowPresenter?.Dispose();
        try { _whisperTask?.Result?.Dispose(); } catch { /* yoksay */ }
        _http.Dispose();
    }
}
