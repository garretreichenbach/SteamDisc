using System.Diagnostics;

namespace SteamDisc.Core.Protocol;

/// <summary>
/// Indirection over process launching so that the install engine's interaction with Steam
/// can be asserted in tests without a Steam client present.
/// </summary>
public interface IProcessRunner
{
    /// <summary>Starts a process without waiting for it.</summary>
    void Start(ProcessLaunch launch);

    /// <summary>Runs a process to completion and captures its output.</summary>
    Task<ProcessResult> RunAsync(ProcessLaunch launch, CancellationToken cancellationToken = default);
}

/// <param name="FileName">Executable, or a URI when <paramref name="UseShellExecute"/> is set.</param>
/// <param name="Arguments">Arguments, already split.</param>
/// <param name="WorkingDirectory">Working directory, or null to inherit.</param>
/// <param name="UseShellExecute">True to hand the target to the OS shell (required for URIs).</param>
/// <param name="Timeout">Optional wall-clock limit for <see cref="IProcessRunner.RunAsync"/>.</param>
public sealed record ProcessLaunch(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    bool UseShellExecute = false,
    TimeSpan? Timeout = null)
{
    public static ProcessLaunch Of(string fileName, params string[] arguments)
        => new(fileName, arguments);
}

/// <param name="ExitCode">Process exit code, or -1 when it had to be killed on timeout.</param>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Success => ExitCode == 0;
}

/// <summary>Default <see cref="IProcessRunner"/> backed by <see cref="Process"/>.</summary>
public sealed class SystemProcessRunner : IProcessRunner
{
    public static SystemProcessRunner Instance { get; } = new();

    public void Start(ProcessLaunch launch)
    {
        using var process = new Process { StartInfo = BuildStartInfo(launch, redirect: false) };
        process.Start();
    }

    public async Task<ProcessResult> RunAsync(ProcessLaunch launch, CancellationToken cancellationToken = default)
    {
        using var process = new Process { StartInfo = BuildStartInfo(launch, redirect: true) };
        process.Start();

        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutSource = launch.Timeout is { } timeout
            ? new CancellationTokenSource(timeout)
            : null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource?.Token ?? CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new ProcessResult(-1, await standardOutput.ConfigureAwait(false), "Timed out.");
        }

        return new ProcessResult(
            process.ExitCode,
            await standardOutput.ConfigureAwait(false),
            await standardError.ConfigureAwait(false));
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or SystemException)
        {
            // Already gone, or we lack the rights — nothing useful to do either way.
        }
    }

    private static ProcessStartInfo BuildStartInfo(ProcessLaunch launch, bool redirect)
    {
        var info = new ProcessStartInfo
        {
            FileName = launch.FileName,
            UseShellExecute = launch.UseShellExecute,
            CreateNoWindow = !launch.UseShellExecute,
            RedirectStandardOutput = redirect && !launch.UseShellExecute,
            RedirectStandardError = redirect && !launch.UseShellExecute,
        };

        foreach (var argument in launch.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        if (launch.WorkingDirectory is not null)
        {
            info.WorkingDirectory = launch.WorkingDirectory;
        }

        return info;
    }
}
