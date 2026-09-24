namespace NetworkOptimizer.Core;

public sealed class FileAppLog : IAppLog
{
    private readonly object _gate = new();
    private readonly string _directory;

    public FileAppLog(AppEnvironment env)
    {
        _directory = env.LogsDir;
        Directory.CreateDirectory(_directory);
    }

    public FileAppLog(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(_directory);
    }

    public void Info(string message) => Write("INFO", Sanitize(message), null);
    public void Warn(string message) => Write("WARN", Sanitize(message), null);
    public void Error(string message, Exception? ex = null) => Write("ERROR", Sanitize(message), ex);

    private void Write(string level, string message, Exception? ex)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}";
        if (ex is not null)
        {
            line += $" :: {ex.GetType().Name}: {Sanitize(ex.Message)}";
        }

        var path = Path.Combine(_directory, $"network-optimizer-{DateTime.Now:yyyyMMdd}.log");
        lock (_gate)
        {
            File.AppendAllText(path, line + Environment.NewLine);
        }
    }

    internal static string Sanitize(string message)
    {
        if (string.IsNullOrEmpty(message)) return message;
        var redacted = message;
        redacted = System.Text.RegularExpressions.Regex.Replace(
            redacted,
            "(password|passwd|pwd|token|secret|authorization|cookie|set-cookie|credential)(=|:|\\s+)[^\\s,;]+",
            "$1=***",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        redacted = System.Text.RegularExpressions.Regex.Replace(
            redacted,
            "(Bearer)\\s+[A-Za-z0-9\\-._~+/]+=*",
            "$1 ***",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return redacted;
    }
}

public sealed class NullLog : IAppLog
{
    public static NullLog Instance { get; } = new();
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message, Exception? ex = null) { }
}

public sealed class MemoryLog : IAppLog
{
    public List<string> Lines { get; } = new();
    public void Info(string message) => Lines.Add("INFO " + message);
    public void Warn(string message) => Lines.Add("WARN " + message);
    public void Error(string message, Exception? ex = null) =>
        Lines.Add("ERROR " + message + (ex is null ? "" : " " + ex.Message));
}
