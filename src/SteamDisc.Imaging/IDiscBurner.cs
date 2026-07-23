using SteamDisc.Core.Diagnostics;
using SteamDisc.Core.Protocol;

namespace SteamDisc.Imaging;

/// <summary>Burns a finished image to physical media.</summary>
public interface IDiscBurner
{
    string Id { get; }

    string Name { get; }

    bool IsAvailable { get; }

    /// <summary>
    /// Starts a burn. Implementations may hand off to an external program, in which case the
    /// returned task completes once that program has been launched, not once the burn is done.
    /// </summary>
    Task<BurnResult> BurnAsync(string isoPath, CancellationToken cancellationToken = default);
}

/// <param name="Started">Whether a burn was started at all.</param>
/// <param name="Delegated">True when the burn was handed to an external program.</param>
/// <param name="Message">Something to tell the user.</param>
public sealed record BurnResult(bool Started, bool Delegated, string Message);

/// <summary>
/// Hands the image to Windows' built-in Disc Image Burner (<c>isoburn.exe</c>).
/// </summary>
/// <remarks>
/// The plan's spike S5 asks whether to drive IMAPI2 through COM interop or delegate. This is
/// the delegate answer, and it is the one shipped first: <c>isoburn</c> is present on every
/// supported Windows, understands BD-R, and its dialog already handles media detection,
/// speed selection and verification. Driving IMAPI2 directly buys progress reporting inside
/// our own window — worth doing later, not worth blocking M4 on.
/// </remarks>
public sealed class WindowsIsoBurnHandoff : IDiscBurner
{
    private readonly IProcessRunner _runner;

    public WindowsIsoBurnHandoff(IProcessRunner? runner = null) => _runner = runner ?? SystemProcessRunner.Instance;

    public string Id => "isoburn";

    public string Name => "Windows Disc Image Burner";

    public bool IsAvailable => OperatingSystem.IsWindows() && File.Exists(ExecutablePath);

    private static string ExecutablePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "isoburn.exe");

    public Task<BurnResult> BurnAsync(string isoPath, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        if (!IsAvailable)
        {
            return Task.FromResult(new BurnResult(
                false, false, "The Windows Disc Image Burner is not available on this machine."));
        }

        _runner.Start(new ProcessLaunch(ExecutablePath, new[] { "/Q", Path.GetFullPath(isoPath) }));

        return Task.FromResult(new BurnResult(
            true,
            true,
            "The Windows Disc Image Burner has been opened. Choose your drive there and start the burn."));
    }
}

/// <summary>Hands the image to an external burner such as ImgBurn.</summary>
public sealed class ExternalBurnerHandoff : IDiscBurner
{
    private readonly IProcessRunner _runner;
    private readonly string _executablePath;
    private readonly IReadOnlyList<string> _argumentTemplate;

    /// <param name="executablePath">Path to the burner executable.</param>
    /// <param name="argumentTemplate">
    /// Arguments, where the token <c>{iso}</c> is replaced with the image path. The ImgBurn
    /// equivalent is <c>-MODE WRITE -SRC {iso} -DEST -VERIFY YES</c>.
    /// </param>
    public ExternalBurnerHandoff(
        string executablePath,
        IReadOnlyList<string>? argumentTemplate = null,
        IProcessRunner? runner = null)
    {
        _executablePath = executablePath;
        _argumentTemplate = argumentTemplate ?? new[] { "{iso}" };
        _runner = runner ?? SystemProcessRunner.Instance;
    }

    public string Id => "external";

    public string Name => Path.GetFileNameWithoutExtension(_executablePath);

    public bool IsAvailable => File.Exists(_executablePath);

    public Task<BurnResult> BurnAsync(string isoPath, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        if (!IsAvailable)
        {
            return Task.FromResult(new BurnResult(false, false, $"'{_executablePath}' was not found."));
        }

        var arguments = _argumentTemplate
            .Select(a => a.Replace("{iso}", Path.GetFullPath(isoPath), StringComparison.Ordinal))
            .ToList();

        _runner.Start(new ProcessLaunch(_executablePath, arguments));
        return Task.FromResult(new BurnResult(true, true, $"{Name} has been started."));
    }
}

/// <summary>Picks a burner, or reports that none is available.</summary>
public static class DiscBurners
{
    public static IReadOnlyList<IDiscBurner> Discover(
        string? externalBurnerPath = null,
        ISteamDiscLogger? logger = null)
    {
        var burners = new List<IDiscBurner>();

        if (!string.IsNullOrWhiteSpace(externalBurnerPath))
        {
            burners.Add(new ExternalBurnerHandoff(externalBurnerPath));
        }

        var windows = new WindowsIsoBurnHandoff();
        if (windows.IsAvailable)
        {
            burners.Add(windows);
        }

        foreach (var candidate in ImgBurnCandidatePaths())
        {
            if (File.Exists(candidate))
            {
                burners.Add(new ExternalBurnerHandoff(
                    candidate,
                    new[] { "-MODE", "WRITE", "-SRC", "{iso}", "-DEST", "-VERIFY", "YES" }));
                break;
            }
        }

        logger?.Debug($"Discovered {burners.Count} burner(s).");
        return burners;
    }

    private static IEnumerable<string> ImgBurnCandidatePaths()
    {
        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }

        foreach (var variable in new[] { "ProgramFiles(x86)", "ProgramFiles" })
        {
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } programFiles)
            {
                yield return Path.Combine(programFiles, "ImgBurn", "ImgBurn.exe");
            }
        }
    }
}
