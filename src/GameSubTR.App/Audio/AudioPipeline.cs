using System.Threading.Channels;
using GameSubTR.Core.Audio;
using GameSubTR.Core.Logging;
using GameSubTR.Core.Models;

namespace GameSubTR.App.Audio;

/// <summary>
/// Oyun sesi → 16 kHz mono → Silero VAD segmentleri → Whisper → İngilizce metin.
/// </summary>
public sealed class AudioPipeline : IDisposable
{
    private readonly WhisperTranscriber _whisper;
    private readonly ProcessLoopbackCapture _capture;
    private readonly StreamingResampler _resampler;
    private readonly SileroVad _vad;
    private readonly SpeechSegmenter _segmenter;
    private readonly Channel<float[]> _segments = Channel.CreateBounded<float[]>(new BoundedChannelOptions(3) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    private readonly object _feedGate = new();

    public event Action<string>? TextReady;
    public event Action<string>? Error;

    public AudioPipeline(int pid, AudioSettings settings, WhisperTranscriber whisper)
    {
        _whisper = whisper;
        _vad = new SileroVad(ModelManager.SileroPath);
        _segmenter = new SpeechSegmenter(_vad, new SpeechSegmenterOptions
        {
            StartThreshold = settings.VadThreshold,
            EndThreshold = Math.Max(0.1f, settings.VadThreshold - 0.15f),
            MinSilenceMs = settings.MinSilenceMs,
            MaxSegmentMs = settings.MaxSegmentMs
        });
        _segmenter.SegmentReady += seg => _segments.Writer.TryWrite(seg);
        _resampler = new StreamingResampler(ProcessLoopbackCapture.SampleRate, ProcessLoopbackCapture.Channels, isFloat: false);

        // "Sadece oyun": oyunun süreç ağacı. Değilse: kendimiz hariç tüm sistem.
        _capture = settings.CaptureGameOnly
            ? new ProcessLoopbackCapture(pid, excludeTarget: false)
            : new ProcessLoopbackCapture(Environment.ProcessId, excludeTarget: true);
        _capture.DataAvailable += OnData;
        _capture.Failed += ex => Error?.Invoke("Ses yakalanamadı: " + ex.Message);

        _worker = Task.Run(() => WorkerAsync(_cts.Token));
        _capture.Start();
    }

    private void OnData(byte[] buffer, int count)
    {
        try
        {
            lock (_feedGate)
            {
                var samples = _resampler.Process(buffer.AsSpan(0, count));
                if (samples.Length > 0) _segmenter.Push(samples);
            }
        }
        catch (Exception ex) { Log.Error("Ses işleme", ex); }
    }

    private async Task WorkerAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var seg in _segments.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var raw = await _whisper.TranscribeAsync(seg, ct).ConfigureAwait(false);
                    var text = TranscriptFilter.Clean(raw);
                    Log.Info($"Whisper ({seg.Length / 16} ms ses, {sw.ElapsedMilliseconds} ms): \"{raw}\"");
                    if (text.Length > 0) TextReady?.Invoke(text);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Log.Error("Whisper", ex); }
            }
        }
        catch (OperationCanceledException) { /* kapanış */ }
    }

    public void Dispose()
    {
        _capture.Dispose();
        _cts.Cancel();
        try { _worker.Wait(2000); } catch { /* yoksay */ }
        _vad.Dispose();
        _cts.Dispose();
    }
}
