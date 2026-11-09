using GameSubTR.Core.Audio;
using Xunit;

namespace GameSubTR.Core.Tests;

public class AudioTests
{
    private sealed class FakeVad(Func<int, float> probAtWindow) : IVoiceActivityDetector
    {
        private int _n;
        public int WindowSize => 512;
        public float Predict(ReadOnlySpan<float> window) => probAtWindow(_n++);
        public void Reset() => _n = 0;
    }

    [Fact]
    public void Resampler_44k_Stereo16_To16kMono_LengthAndDc()
    {
        var r = new StreamingResampler(44100, 2, isFloat: false);
        // 1 saniye, sabit 0.5 genlik.
        var bytes = new byte[44100 * 4];
        short v = 16384;
        for (int i = 0; i < 44100 * 2; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 2), v);
        var total = new List<float>();
        // Parça parça besle (akış durumunu test et).
        for (int off = 0; off < bytes.Length; off += 4 * 1000)
            total.AddRange(r.Process(bytes.AsSpan(off, Math.Min(4 * 1000, bytes.Length - off))));
        Assert.InRange(total.Count, 15990, 16000);
        Assert.InRange(total[^100], 0.49f, 0.51f);
    }

    [Fact]
    public void Resampler_Float48k_PreservesSineFrequencyRoughly()
    {
        var r = new StreamingResampler(48000, 1, isFloat: true);
        var bytes = new byte[48000 * 4];
        for (int i = 0; i < 48000; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), (float)Math.Sin(2 * Math.PI * 440 * i / 48000));
        var o = r.Process(bytes);
        int crossings = 0;
        for (int i = 1; i < o.Length; i++) if (o[i - 1] < 0 && o[i] >= 0) crossings++;
        Assert.InRange(crossings, 430, 450);
    }

    [Fact]
    public void Segmenter_EmitsOneSegmentForSpeechBurst()
    {
        // pencere 0-9 sessiz, 10-49 konuşma (~1.3 sn), sonra sessiz.
        var vad = new FakeVad(n => n is >= 10 and < 50 ? 0.9f : 0.05f);
        var seg = new SpeechSegmenter(vad, new SpeechSegmenterOptions { MinSilenceMs = 500 });
        var outSegs = new List<float[]>();
        seg.SegmentReady += s => outSegs.Add(s);
        seg.Push(new float[512 * 100]);
        Assert.Single(outSegs);
        // ~40 pencere konuşma + ön kayıt (≈250ms) + son kayıt (150ms)
        Assert.InRange(outSegs[0].Length, 512 * 40, 512 * 40 + 16000 * 450 / 1000);
    }

    [Fact]
    public void Segmenter_IgnoresTooShortBlips_AndCutsLongSpeech()
    {
        var vad = new FakeVad(n => n == 5 ? 0.9f : (n >= 20 ? 0.9f : 0.0f));
        var seg = new SpeechSegmenter(vad, new SpeechSegmenterOptions { MaxSegmentMs = 2000, MinSpeechMs = 300 });
        var outSegs = new List<float[]>();
        seg.SegmentReady += s => outSegs.Add(s);
        seg.Push(new float[512 * 200]);
        Assert.True(outSegs.Count >= 2);                    // uzun konuşma 2 sn'de bölündü
        Assert.All(outSegs, s => Assert.True(s.Length >= 16000 * 300 / 1000)); // kısa sinyal elendi
    }

    [Fact]
    public void TranscriptFilter_RemovesHallucinations()
    {
        Assert.Equal("", TranscriptFilter.Clean(" Thank you."));
        Assert.Equal("", TranscriptFilter.Clean("[Music]"));
        Assert.Equal("", TranscriptFilter.Clean("you you you you you you you you"));
        Assert.Equal("Get down!", TranscriptFilter.Clean("(gunfire) Get down!"));
        Assert.Equal("We ride at dawn.", TranscriptFilter.Clean("  We ride at dawn. "));
    }
}
