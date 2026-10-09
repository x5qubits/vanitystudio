using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;

namespace VanityStudio.Infra;

/// <summary>Process-wide diagnostic log. Lines are queued and flushed to one file under the agent's home folder
/// (see <see cref="AgentConfig.LogsDir"/>); with <see cref="Verbose"/> they are echoed to the console too.</summary>
public static class Log
{
    private static readonly ConcurrentQueue<string> Queue = new();
    private static readonly object FlushGate = new();
    private static string? _path;
    private static Timer? _timer;

    /// <summary>Echo every line to stderr (the `--verbose` switch).</summary>
    public static bool Verbose { get; set; }

    public static void Initialize(string path)
    {
        _path = path;
        try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); } catch { }
        _timer ??= new Timer(_ => Flush(), null, 1000, 1000);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
    }

    public static void Debug(string message, [CallerMemberName] string method = "", [CallerFilePath] string file = "") => Write("DEBUG", message, method, file);
    public static void Info(string message, [CallerMemberName] string method = "", [CallerFilePath] string file = "") => Write("INFO", message, method, file);
    public static void Warn(string message, [CallerMemberName] string method = "", [CallerFilePath] string file = "") => Write("WARN", message, method, file);
    public static void Error(string message, [CallerMemberName] string method = "", [CallerFilePath] string file = "") => Write("ERROR", message, method, file);
    public static void Error(Exception ex, [CallerMemberName] string method = "", [CallerFilePath] string file = "") => Write("ERROR", ex.ToString(), method, file);

    private static void Write(string level, string message, string method, string file)
    {
        var sep = file.LastIndexOfAny(['\\', '/']);
        var name = sep >= 0 ? file[(sep + 1)..] : file;
        var dot = name.LastIndexOf('.');
        if (dot > 0) name = name[..dot];
        var line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{name}.{method}] {message}";
        Queue.Enqueue(line);
        if (Verbose && level != "DEBUG")
        {
            try { Console.Error.WriteLine("  · " + line); } catch { }
        }
    }

    public static void Flush()
    {
        if (_path is null || Queue.IsEmpty) return;
        lock (FlushGate)
        {
            var sb = new StringBuilder();
            while (Queue.TryDequeue(out var line)) sb.AppendLine(line);
            if (sb.Length == 0) return;
            try { File.AppendAllText(_path, sb.ToString(), new UTF8Encoding(false)); } catch { }
        }
    }
}
