using SteamDisc.Core.Diagnostics;
using SteamDisc.Core.Payload;
using SteamDisc.Core.Protocol;

namespace SteamDisc.Install;

/// <summary>
/// Runs the redistributables that ship inside a game folder — the <c>_CommonRedist</c> tree
/// that Steam normally executes on first install.
/// </summary>
public sealed class PrerequisiteRunner
{
    private readonly IProcessRunner _runner;
    private readonly ISteamDiscLogger _logger;

    public PrerequisiteRunner(IProcessRunner? runner = null, ISteamDiscLogger? logger = null)
    {
        _runner = runner ?? SystemProcessRunner.Instance;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Runs each prerequisite in order and returns the ones that failed.
    /// </summary>
    /// <remarks>
    /// Failures are collected rather than thrown. A missing VC++ runtime is a problem the user
    /// can fix in a minute; discarding a 40 GB install that is otherwise complete because a
    /// redistributable returned a non-zero code would not be a service to anyone.
    /// </remarks>
    public async Task<IReadOnlyList<PrerequisiteFailure>> RunAsync(
        IReadOnlyList<PrerequisiteDescriptor> prerequisites,
        string installRoot,
        CancellationToken cancellationToken = default)
    {
        var failures = new List<PrerequisiteFailure>();

        foreach (var prerequisite in prerequisites)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsApplicable(prerequisite))
            {
                _logger.Debug($"Skipping prerequisite '{prerequisite.Name}' (platform {prerequisite.Platform}).");
                continue;
            }

            var path = Path.Combine(installRoot, prerequisite.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                failures.Add(new PrerequisiteFailure(prerequisite, -1, $"'{prerequisite.Path}' was not found."));
                continue;
            }

            _logger.Info($"Running prerequisite '{prerequisite.Name}'.");

            var arguments = SplitArguments(prerequisite.Args);
            ProcessResult result;
            try
            {
                result = await _runner
                    .RunAsync(
                        new ProcessLaunch(
                            path,
                            arguments,
                            Path.GetDirectoryName(path),
                            Timeout: TimeSpan.FromMinutes(15)),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                failures.Add(new PrerequisiteFailure(prerequisite, -1, ex.Message));
                continue;
            }

            if (!prerequisite.SuccessExitCodes.Contains(result.ExitCode))
            {
                failures.Add(new PrerequisiteFailure(
                    prerequisite,
                    result.ExitCode,
                    string.IsNullOrWhiteSpace(result.StandardError)
                        ? $"Exit code {result.ExitCode}."
                        : result.StandardError.Trim()));
            }
        }

        return failures;
    }

    private static bool IsApplicable(PrerequisiteDescriptor prerequisite) => prerequisite.Platform.ToLowerInvariant() switch
    {
        "windows" => OperatingSystem.IsWindows(),
        "linux" => OperatingSystem.IsLinux(),
        "macos" or "osx" => OperatingSystem.IsMacOS(),
        "any" or "" => true,
        _ => false,
    };

    /// <summary>
    /// Splits a command-line string on whitespace, honouring double quotes. Redistributable
    /// argument strings are simple by convention, and this keeps them readable in the manifest.
    /// </summary>
    internal static IReadOnlyList<string> SplitArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return Array.Empty<string>();
        }

        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        foreach (var c in arguments)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }
}

/// <param name="Prerequisite">The descriptor that failed.</param>
/// <param name="ExitCode">Its exit code, or -1 when it could not be started.</param>
/// <param name="Message">Something to show the user.</param>
public sealed record PrerequisiteFailure(PrerequisiteDescriptor Prerequisite, int ExitCode, string Message)
{
    public override string ToString() => $"{Prerequisite.Name}: {Message}";
}
