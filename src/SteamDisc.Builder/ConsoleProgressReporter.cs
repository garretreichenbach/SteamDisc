using System.Globalization;
using SteamDisc.Core.Progress;

namespace SteamDisc.Builder;

/// <summary>Single-line progress display for long Builder operations.</summary>
public sealed class ConsoleProgressReporter : IProgress<OperationProgress>
{
    private const int BarWidth = 30;

    private readonly object _gate = new();
    private readonly bool _interactive = !Console.IsOutputRedirected;
    private string _lastLine = string.Empty;
    private OperationPhase _lastPhase = (OperationPhase)(-1);

    public void Report(OperationProgress value)
    {
        lock (_gate)
        {
            if (!_interactive)
            {
                if (value.Phase != _lastPhase)
                {
                    _lastPhase = value.Phase;
                    Console.WriteLine($"  {value.Phase}...");
                }

                return;
            }

            var line = Build(value);
            if (line == _lastLine)
            {
                return;
            }

            _lastLine = line;
            var width = Math.Max(20, SafeWidth() - 1);
            Console.Write('\r' + (line.Length > width ? line[..width] : line).PadRight(width));
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            if (_interactive && _lastLine.Length > 0)
            {
                Console.WriteLine();
                _lastLine = string.Empty;
            }
        }
    }

    private static string Build(OperationProgress progress)
    {
        var phase = progress.Phase.ToString().PadRight(14);

        if (progress.Fraction is not { } fraction)
        {
            return phase + (progress.CurrentItem ?? string.Empty);
        }

        var filled = (int)Math.Round(fraction * BarWidth);
        var bar = new string('#', filled) + new string('.', BarWidth - filled);
        var percent = (fraction * 100).ToString("F1", CultureInfo.InvariantCulture);

        var rate = progress.BytesPerSecond is { } bytesPerSecond and > 0
            ? "  " + Commands.Format.Bytes((long)bytesPerSecond) + "/s"
            : string.Empty;

        var eta = progress.EstimatedRemaining is { } remaining
            ? "  ETA " + Commands.Format.Duration(remaining)
            : string.Empty;

        return $"{phase}[{bar}] {percent,5}%{rate}{eta}";
    }

    private static int SafeWidth()
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
}
