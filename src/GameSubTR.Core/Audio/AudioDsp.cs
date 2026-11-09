namespace GameSubTR.Core.Audio;

/// <summary>
/// Çok kanallı PCM16/Float32 akışı → 16 kHz mono float.
/// Akış halinde çalışır (parçalar arası durum korunur). Önce basit alçak geçiren
/// (kutu) filtre, sonra doğrusal interpolasyonla yeniden örnekleme.
/// </summary>
public sealed class StreamingResampler
{
    private readonly int _inRate, _channels, _outRate;
    private readonly bool _isFloat;
    private readonly double _step;
    private double _pos;            // bir sonraki çıktı örneğinin giriş konumu (_history başlangıcına göre)
    private readonly List<float> _history = new();
    private readonly int _boxLen;
    private readonly Queue<float> _box = new();
    private double _boxSum;

    public StreamingResampler(int inRate, int channels, bool isFloat, int outRate = 16000)
    {
        if (inRate <= 0 || channels <= 0) throw new ArgumentOutOfRangeException(nameof(inRate));
        _inRate = inRate; _channels = channels; _isFloat = isFloat; _outRate = outRate;
        _step = (double)inRate / outRate;
        _boxLen = Math.Max(1, (int)Math.Round(_step)); // yaklaşık Nyquist'e göre ortalama
    }

    public int InRate => _inRate;

    /// <summary>Ham bayt bloğunu işler, üretilen 16 kHz mono örnekleri döndürür.</summary>
    public float[] Process(ReadOnlySpan<byte> data)
    {
        int bytesPerSample = _isFloat ? 4 : 2;
        int frameBytes = bytesPerSample * _channels;
        int frames = data.Length / frameBytes;
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            for (int c = 0; c < _channels; c++)
            {
                int o = f * frameBytes + c * bytesPerSample;
                sum += _isFloat ? BitConverter.ToSingle(data.Slice(o, 4)) : BitConverter.ToInt16(data.Slice(o, 2)) / 32768.0;
            }
            float mono = (float)(sum / _channels);
            _box.Enqueue(mono); _boxSum += mono;
            if (_box.Count > _boxLen) _boxSum -= _box.Dequeue();
            _history.Add((float)(_boxSum / _box.Count));
        }

        var output = new List<float>(frames * _outRate / _inRate + 2);
        while (_pos + 1 < _history.Count)
        {
            int i = (int)_pos;
            double t = _pos - i;
            output.Add((float)(_history[i] * (1 - t) + _history[i + 1] * t));
            _pos += _step;
        }
        // _pos, adım > 1 olduğunda tampon sonunu geçebilir; sadece gerçekten silinen kadar kaydır.
        int removed = Math.Min(Math.Max(0, (int)_pos), _history.Count);
        if (removed > 0)
        {
            _history.RemoveRange(0, removed);
            _pos -= removed;
        }
        return output.ToArray();
    }

    /// <summary>Silence (sessizlik) bayrağı gelen paketler için sıfır örnek üretir.</summary>
    public float[] ProcessSilence(int frames)
    {
        int bytesPerSample = _isFloat ? 4 : 2;
        return Process(new byte[frames * bytesPerSample * _channels]);
    }
}

/// <summary>16 kHz'de 512 örneklik pencerelerde konuşma olasılığı veren dedektör.</summary>
public interface IVoiceActivityDetector
{
    int WindowSize { get; }
    float Predict(ReadOnlySpan<float> window);
    void Reset();
}

public sealed class SpeechSegmenterOptions
{
    public float StartThreshold { get; set; } = 0.5f;
    public float EndThreshold { get; set; } = 0.35f;
    public int MinSilenceMs { get; set; } = 550;
    public int MinSpeechMs { get; set; } = 300;
    public int MaxSegmentMs { get; set; } = 12000;
    public int PreRollMs { get; set; } = 250;
    public int PostRollMs { get; set; } = 150;
}

/// <summary>
/// VAD olasılıklarından konuşma segmentleri üretir. Her tamamlanan segment
/// (16 kHz mono float) <see cref="SegmentReady"/> ile yayınlanır.
/// </summary>
public sealed class SpeechSegmenter
{
    private const int Rate = 16000;
    private readonly IVoiceActivityDetector _vad;
    private readonly SpeechSegmenterOptions _o;
    private readonly List<float> _pending = new();
    private readonly LinkedList<float[]> _preRoll = new();
    private int _preRollSamples;
    private readonly List<float> _segment = new();
    private bool _inSpeech;
    private int _silenceSamples;
    private int _speechSamples;

    public event Action<float[]>? SegmentReady;

    public SpeechSegmenter(IVoiceActivityDetector vad, SpeechSegmenterOptions? options = null)
    {
        _vad = vad; _o = options ?? new SpeechSegmenterOptions();
    }

    private static int Ms(int ms) => ms * Rate / 1000;

    public void Push(ReadOnlySpan<float> samples)
    {
        foreach (var s in samples) _pending.Add(s);
        int win = _vad.WindowSize;
        int offset = 0;
        while (_pending.Count - offset >= win)
        {
            var window = new float[win];
            _pending.CopyTo(offset, window, 0, win);
            offset += win;
            ProcessWindow(window);
        }
        if (offset > 0) _pending.RemoveRange(0, offset);
    }

    private void ProcessWindow(float[] window)
    {
        float p = _vad.Predict(window);
        if (!_inSpeech)
        {
            if (p >= _o.StartThreshold)
            {
                _inSpeech = true;
                _segment.Clear();
                foreach (var chunk in _preRoll) _segment.AddRange(chunk);
                _preRoll.Clear(); _preRollSamples = 0;
                _segment.AddRange(window);
                _silenceSamples = 0;
                _speechSamples = window.Length;
            }
            else
            {
                _preRoll.AddLast(window); _preRollSamples += window.Length;
                while (_preRollSamples - _preRoll.First!.Value.Length >= Ms(_o.PreRollMs))
                {
                    _preRollSamples -= _preRoll.First.Value.Length;
                    _preRoll.RemoveFirst();
                }
            }
            return;
        }

        _segment.AddRange(window);
        if (p < _o.EndThreshold) _silenceSamples += window.Length;
        else { _silenceSamples = 0; _speechSamples += window.Length; }

        if (_silenceSamples >= Ms(_o.MinSilenceMs) || _segment.Count >= Ms(_o.MaxSegmentMs))
            Finish();
    }

    private void Finish()
    {
        _inSpeech = false;
        // Sondaki sessizliği kırp (post-roll kadar bırak).
        int trim = Math.Max(0, _silenceSamples - Ms(_o.PostRollMs));
        int len = Math.Max(0, _segment.Count - trim);
        if (_speechSamples >= Ms(_o.MinSpeechMs) && len > 0)
            SegmentReady?.Invoke(_segment.GetRange(0, len).ToArray());
        _segment.Clear();
        _silenceSamples = 0; _speechSamples = 0;
    }

    /// <summary>Devam eden segmenti zorla bitirir (ör. kapanışta).</summary>
    public void Flush()
    {
        if (_inSpeech) Finish();
    }

    public void Reset()
    {
        _pending.Clear(); _preRoll.Clear(); _preRollSamples = 0; _segment.Clear();
        _inSpeech = false; _silenceSamples = 0; _speechSamples = 0; _vad.Reset();
    }
}
