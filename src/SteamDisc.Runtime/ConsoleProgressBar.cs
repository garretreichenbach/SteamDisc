using System.Globalization;
using SteamDisc.Core.Progress;

namespace SteamDisc.Runtime;

/// <summary>
/// Renders install progress on one console line.
/// </summary>
/// <remarks>
/// The plan is blunt about this: at BD-R speeds a large install takes twenty-five minutes
/// before decompression, and "progress UI must be honest and the estimate must not lie". So
/// the remaining-time figure is shown only once the underlying tracker is willing to commit to
/// one, and the bar shows nothing rather than a fabricated percentage when the total is unknown.
/// </remarks>
public sealed class ConsoleProgressBar : IProgress<OperationProgress>
{
    private const int BarWidth = 32;

    private readonly object _gate = new();
    private readonly bool _enabled;
    private string _lastLine = string.Empty;
    private OperationPhase _lastPhase = (OperationPhase)(-1);

    public ConsoleProgressBar()
    {
        // Redirected output means a log file, where a rewriting progress bar is just noise.
        _enabled = !Console.IsOutputRedirected;
    }

    public void Report(OperationProgress value)
    {
        lock (_gate)
        {
            if (!_enabled)
            {
                if (value.Phase != _lastPhase)
                {
                    _lastPhase = value.Phase;
                    Console.WriteLine(Describe(value.Phase));
                }

                return;
            }

            var line = BuildLine(value);
            if (line == _lastLine)
            {
                return;
            }

            _lastLine = line;

            var width = Math.Max(20, SafeWindowWidth() - 1);
            if (line.Length > width)
            {
                line = line[..width];
            }

            Console.Write('\r' + line.PadRight(width));
        }
    }

    /// <summary>Ends the progress line so subsequent output starts cleanly.</summary>
    public void Complete()
    {
        lock (_gate)
        {
            if (_enabled && _lastLine.Length > 0)
            {
                Console.WriteLine();
                _lastLine = string.Empty;
            }
        }
    }

    private static string BuildLine(OperationProgress progress)
    {
        var phase = Describe(progress.Phase);

        if (progress.Fraction is not { } fraction)
        {
            return progress.CurrentItem is { Length: > 0 } item
                ? $"{phase}  {Truncate(item, 40)}"
                : phase;
        }

        var filled = (int)Math.Round(fraction * BarWidth);
        var bar = new string('#', filled) + new string('.', BarWidth - filled);
        var percent = (fraction * 100).ToString("F1", CultureInfo.InvariantCulture);

        var rate = progress.BytesPerSecond is { } bytesPerSecond and > 0
            ? "  " + FormatBytes((long)bytesPerSecond) + "/s"
            : string.Empty;

        var remaining = progress.EstimatedRemaining is { } eta
            ? "  ETA " + FormatDuration(eta)
            : string.Empty;

        return $"{phase} [{bar}] {percent,5}%{rate}{remaining}";
    }

    private static string Describe(OperationPhase phase) => phase switch
    {
        OperationPhase.Preparing => "Preparing      ",
        OperationPhase.Scanning => "Scanning       ",
        OperationPhase.Hashing => "Hashing        ",
        OperationPhase.Compressing => "Compressing    ",
        OperationPhase.Extracting => "Installing     ",
        OperationPhase.Verifying => "Verifying      ",
        OperationPhase.WritingManifest => "Registering    ",
        OperationPhase.RunningPrerequisites => "Prerequisites  ",
        OperationPhase.BuildingImage => "Building image ",
        OperationPhase.Burning => "Burning        ",
        _ => "Finishing      ",
    };

    private static int SafeWindowWidth()
    {
        try
        {
            return Console.WindowWidth;
        }
        catch (IOException)
        {
            return 80;
        }
    }

    private static string Truncate(string value, int length)
        => value.Length <= length ? value : "…" + value[^(length - 1)..];

    internal static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString(value >= 100 ? "F0" : "F1", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    internal static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1
        ? duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
        : duration.ToString(@"m\:ss", CultureInfo.InvariantCulture);
}
