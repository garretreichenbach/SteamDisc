using SteamDisc.Core.Theming;
using SteamDisc.Install;

namespace SteamDisc.Runtime;

/// <summary>
/// The console front-end's implementation of <see cref="IInstallHost"/>.
/// </summary>
/// <remarks>
/// Deliberately the plainest possible surface. The skinned Avalonia runtime will implement the
/// same interface; keeping this one working means there is always a fallback that runs on a
/// machine where a GUI cannot start, and it is what makes the engine testable.
/// </remarks>
public sealed class ConsoleInstallHost : IInstallHost
{
    private readonly Theme _theme;
    private readonly bool _assumeYes;

    public ConsoleInstallHost(Theme theme, bool assumeYes = false)
    {
        _theme = theme;
        _assumeYes = assumeYes;
    }

    public Task<string?> RequestDiscAsync(DiscRequest request, CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine(new string('-', 60));

        if (request.Reason is { Length: > 0 } reason)
        {
            Console.WriteLine($"  {reason}");
        }

        Console.WriteLine("  " + _theme.String(
            ThemeStrings.InsertNextDisc,
            new Dictionary<string, string>
            {
                ["disc"] = request.DiscNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["discCount"] = request.DiscCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["title"] = request.Title,
            }));

        Console.WriteLine(new string('-', 60));
        Console.WriteLine("Type the drive or folder path and press Enter, or leave blank to cancel.");
        Console.Write("> ");

        cancellationToken.ThrowIfCancellationRequested();
        var answer = Console.ReadLine()?.Trim().Trim('"');

        return Task.FromResult(string.IsNullOrWhiteSpace(answer) ? null : answer);
    }

    public void ReportWarning(string message)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Error.WriteLine("  ! " + message);
        Console.ForegroundColor = previous;
    }

    public Task<bool> ConfirmAsync(string question, CancellationToken cancellationToken)
    {
        if (_assumeYes)
        {
            Console.WriteLine($"{question} [assuming yes]");
            return Task.FromResult(true);
        }

        Console.Write($"{question} [y/N] ");
        cancellationToken.ThrowIfCancellationRequested();

        var answer = Console.ReadLine()?.Trim();
        return Task.FromResult(answer is not null &&
                               (answer.Equals("y", StringComparison.OrdinalIgnoreCase) ||
                                answer.Equals("yes", StringComparison.OrdinalIgnoreCase)));
    }
}
