using System.Globalization;

namespace SteamDisc.Core.Diagnostics;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>
/// Minimal logging seam. The disc runtime cannot assume a logging framework is worth its
/// bytes, and a failed install on a stranger's machine is only debuggable from the log file
/// it leaves behind, so this is deliberately tiny and always on.
/// </summary>
public interface ISteamDiscLogger
{
    void Log(LogLevel level, string message, Exception? exception = null);
}

public static class LoggerExtensions
{
    public static void Debug(this ISteamDiscLogger logger, string message) => logger.Log(LogLevel.Debug, message);

    public static void Info(this ISteamDiscLogger logger, string message) => logger.Log(LogLevel.Info, message);

    public static void Warn(this ISteamDiscLogger logger, string message, Exception? exception = null)
        => logger.Log(LogLevel.Warning, message, exception);

    public static void Error(this ISteamDiscLogger logger, string message, Exception? exception = null)
        => logger.Log(LogLevel.Error, message, exception);
}

/// <summary>Discards everything. The default when a caller supplies no logger.</summary>
public sealed class NullLogger : ISteamDiscLogger
{
    public static NullLogger Instance { get; } = new();

    public void Log(LogLevel level, string message, Exception? exception = null)
    {
    }
}

/// <summary>Fans a log out to several sinks.</summary>
public sealed class CompositeLogger : ISteamDiscLogger
{
    private readonly IReadOnlyList<ISteamDiscLogger> _loggers;

    public CompositeLogger(params ISteamDiscLogger[] loggers) => _loggers = loggers;

    public void Log(LogLevel level, string message, Exception? exception = null)
    {
        foreach (var logger in _loggers)
        {
            logger.Log(level, message, exception);
        }
    }
}

/// <summary>Appends to a file, flushing every line so a hard crash still leaves a usable trail.</summary>
public sealed class FileLogger : ISteamDiscLogger, IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _gate = new();

    public FileLogger(string path)
    {
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        Path = path;
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true,
        };
    }

    public string Path { get; }

    /// <summary>Conventional log location: beside the user's local app data, never on the disc.</summary>
    public static string DefaultLogPath(string component)
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(root))
        {
            root = System.IO.Path.GetTempPath();
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return System.IO.Path.Combine(root, "SteamDisc", "logs", $"{component}-{stamp}.log");
    }

    public void Log(LogLevel level, string message, Exception? exception = null)
    {
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level,-7}] {message}");

        lock (_gate)
        {
            _writer.WriteLine(line);
            if (exception is not null)
            {
                _writer.WriteLine(exception.ToString());
            }
        }
    }

    public void Dispose() => _writer.Dispose();
}

/// <summary>Writes to the console, routing warnings and errors to stderr.</summary>
public sealed class ConsoleLogger : ISteamDiscLogger
{
    public ConsoleLogger(LogLevel minimumLevel = LogLevel.Info) => MinimumLevel = minimumLevel;

    public LogLevel MinimumLevel { get; set; }

    public void Log(LogLevel level, string message, Exception? exception = null)
    {
        if (level < MinimumLevel)
        {
            return;
        }

        var writer = level >= LogLevel.Warning ? Console.Error : Console.Out;
        writer.WriteLine(level >= LogLevel.Warning ? $"{level}: {message}" : message);
        if (exception is not null && level >= LogLevel.Warning)
        {
            writer.WriteLine(exception);
        }
    }
}
