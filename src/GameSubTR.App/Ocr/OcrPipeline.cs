using GameSubTR.App.Capture;
using GameSubTR.Core.Imaging;
using GameSubTR.Core.Logging;
using GameSubTR.Core.Models;
using GameSubTR.Core.Text;

namespace GameSubTR.App.Ocr;

/// <summary>
/// Yakala → değişiklik kontrolü → ön işleme → OCR → kararlılık döngüsü.
/// Kararlı yeni metin geldiğinde <see cref="TextReady"/>, metin kaybolunca <see cref="TextCleared"/>.
/// </summary>
public sealed class OcrPipeline : IDisposable
{
    private readonly WindowsOcr _ocr;
    private readonly Func<IFrameSource?> _source;
    private readonly Func<GameProfile> _profile;
    private readonly TextStabilizer _stabilizer = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private ImageHash? _lastHash;
    private string _lastRaw = "";
    private volatile bool _resetRequested;

    public event Action<string>? TextReady;
    public event Action? TextCleared;

    public OcrPipeline(WindowsOcr ocr, Func<IFrameSource?> source, Func<GameProfile> profile)
    {
        _ocr = ocr; _source = source; _profile = profile;
    }

    public void Start() => _loop ??= Task.Run(() => LoopAsync(_cts.Token));

    private async Task LoopAsync(CancellationToken ct)
    {
        Log.Info("OCR döngüsü başladı");
        while (!ct.IsCancellationRequested)
        {
            var profile = _profile();
            var started = DateTime.UtcNow;
            try
            {
                await StepAsync(profile).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("OCR adımı", ex);
            }
            var wait = Math.Max(50, profile.OcrIntervalMs) - (int)(DateTime.UtcNow - started).TotalMilliseconds;
            try { await Task.Delay(Math.Max(15, wait), ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task StepAsync(GameProfile profile)
    {
        if (_resetRequested)
        {
            _resetRequested = false;
            _stabilizer.Reset();
            _lastHash = null;
            _lastRaw = "";
        }
        if (profile.Region is not { IsValid: true } region) return;
        var src = _source();
        var frame = src?.Capture(region);
        if (frame == null) return;

        var pre = ImagePreprocessor.Process(frame, profile.ColorFilter);
        string raw;
        if (_lastHash is { } h && h.Distance(pre.Hash) <= 3)
        {
            raw = _lastRaw; // görüntü değişmedi → OCR'ı atla
        }
        else
        {
            raw = await _ocr.RecognizeAsync(pre.Image).ConfigureAwait(false);
            // Eşiklenmiş görüntü boş çıktıysa gri tonla bir kez daha dene.
            if (TextUtil.LetterCount(raw) < 2 && pre.Binarized)
            {
                var gray = ImagePreprocessor.Process(frame, OcrColorFilter.None);
                raw = await _ocr.RecognizeAsync(gray.Image).ConfigureAwait(false);
            }
            _lastHash = pre.Hash;
            _lastRaw = raw;
        }

        var ev = _stabilizer.Push(raw);
        if (ev.Kind == StabilizerEventKind.NewText) TextReady?.Invoke(ev.Text);
        else if (ev.Kind == StabilizerEventKind.Cleared) TextCleared?.Invoke();
    }

    /// <summary>Bölge değişince önceki durumu unut.</summary>
    public void Reset() => _resetRequested = true;

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(1500); } catch { /* yoksay */ }
        _cts.Dispose();
    }
}
