using GameSubTR.Core.Audio;
using GameSubTR.Core.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Whisper.net;

namespace GameSubTR.App.Audio;

/// <summary>Silero VAD (ONNX). 16 kHz, 512 örneklik pencere + 64 örnek bağlam.</summary>
public sealed class SileroVad : IVoiceActivityDetector, IDisposable
{
    private const int Context = 64;
    private readonly InferenceSession _session;
    private readonly float[] _state = new float[2 * 1 * 128];
    private readonly float[] _context = new float[Context];
    private readonly DenseTensor<long> _sr = new(new[] { 16000L }, Array.Empty<int>());

    public int WindowSize => 512;

    public SileroVad(string modelPath)
    {
        var opts = new SessionOptions { InterOpNumThreads = 1, IntraOpNumThreads = 1, GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        _session = new InferenceSession(modelPath, opts);
    }

    public float Predict(ReadOnlySpan<float> window)
    {
        var input = new float[Context + window.Length];
        _context.CopyTo(input, 0);
        window.CopyTo(input.AsSpan(Context));
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input", new DenseTensor<float>(input, new[] { 1, input.Length })),
            NamedOnnxValue.CreateFromTensor("state", new DenseTensor<float>((float[])_state.Clone(), new[] { 2, 1, 128 })),
            NamedOnnxValue.CreateFromTensor("sr", _sr)
        };
        using var results = _session.Run(inputs);
        float prob = 0;
        foreach (var r in results)
        {
            if (r.Name == "output") prob = r.AsEnumerable<float>().First();
            else if (r.Name == "stateN")
            {
                int i = 0;
                foreach (var v in r.AsEnumerable<float>()) { if (i < _state.Length) _state[i++] = v; }
            }
        }
        Array.Copy(input, input.Length - Context, _context, 0, Context);
        return prob;
    }

    public void Reset()
    {
        Array.Clear(_state);
        Array.Clear(_context);
    }

    public void Dispose() => _session.Dispose();
}

/// <summary>Whisper.net (whisper.cpp). GPU için Vulkan, yoksa CPU çalışma zamanı otomatik seçilir.</summary>
public sealed class WhisperTranscriber : IDisposable
{
    private readonly WhisperFactory _factory;
    private readonly WhisperProcessor _processor;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private WhisperTranscriber(WhisperFactory factory, WhisperProcessor processor)
    {
        _factory = factory; _processor = processor;
    }

    public static WhisperTranscriber Load(string modelPath)
    {
        var factory = WhisperFactory.FromPath(modelPath);
        var processor = factory.CreateBuilder()
            .WithLanguage("en")
            .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 2, 8))
            .WithNoContext()
            .Build();
        Log.Info("Whisper modeli yüklendi: " + Path.GetFileName(modelPath));
        return new WhisperTranscriber(factory, processor);
    }

    public async Task<string> TranscribeAsync(float[] samples, CancellationToken ct)
    {
        // whisper.cpp 1 saniyeden kısa seste kararsız; sessizlikle doldur.
        if (samples.Length < 17600)
        {
            var padded = new float[17600];
            samples.CopyTo(padded, 0);
            samples = padded;
        }
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sb = new System.Text.StringBuilder();
            await foreach (var seg in _processor.ProcessAsync(samples, ct).ConfigureAwait(false))
                sb.Append(seg.Text);
            return sb.ToString().Trim();
        }
        finally { _lock.Release(); }
    }

    public void Dispose()
    {
        _processor.Dispose();
        _factory.Dispose();
    }
}

/// <summary>Model dosyalarını indirir/yönetir.</summary>
public static class ModelManager
{
    public static string ModelsDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameSubTR", "models");

    public static string SileroPath
    {
        get
        {
            var bundled = Path.Combine(AppContext.BaseDirectory, "models", "silero_vad.onnx");
            return File.Exists(bundled) ? bundled : Path.Combine(ModelsDir, "silero_vad.onnx");
        }
    }

    public static string WhisperPath(string fileName) => Path.Combine(ModelsDir, fileName);

    public static async Task<string> EnsureWhisperAsync(string fileName, HttpClient http, IProgress<double>? progress, CancellationToken ct)
    {
        var path = WhisperPath(fileName);
        if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) return path;
        Directory.CreateDirectory(ModelsDir);
        var url = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/" + fileName;
        Log.Info("Whisper modeli indiriliyor: " + url);
        var tmp = path + ".part";
        using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? -1;
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = File.Create(tmp);
            var buf = new byte[1 << 16];
            long done = 0; int n; double lastReport = -1;
            while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                done += n;
                if (total > 0)
                {
                    double p = (double)done / total;
                    if (p - lastReport >= 0.01) { lastReport = p; progress?.Report(p); }
                }
            }
        }
        File.Move(tmp, path, true);
        Log.Info("Whisper modeli indirildi: " + path);
        return path;
    }
}
