using System.Collections.Concurrent;

namespace GameSubTR.Core.Logging;

/// <summary>Basit, iş parçacığı güvenli dosya logu.</summary>
public static class Log
{
    private static readonly BlockingCollection<string> Queue = new(4096);
    private static string? _path;
    private static Thread? _writer;

    public static event Action<string>? Written;

    public static void Init(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "gamesubtr.log");
        try
        {
            if (File.Exists(_path) && new FileInfo(_path).Length > 5 * 1024 * 1024)
                File.Move(_path, _path + ".old", true);
        }
        catch { /* yoksay */ }

        _writer = new Thread(() =>
        {
            foreach (var line in Queue.GetConsumingEnumerable())
            {
                try { File.AppendAllText(_path!, line + Environment.NewLine); } catch { /* yoksay */ }
            }
        }) { IsBackground = true, Name = "LogWriter" };
        _writer.Start();
    }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERROR", ex == null ? msg : $"{msg}: {ex}");

    private static void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}";
        Written?.Invoke(line);
        if (_path != null) Queue.TryAdd(line);
    }
}
