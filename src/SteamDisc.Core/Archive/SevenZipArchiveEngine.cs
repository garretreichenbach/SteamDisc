using System.Globalization;
using System.Text.RegularExpressions;
using SteamDisc.Core.Progress;
using SteamDisc.Core.Protocol;

namespace SteamDisc.Core.Archive;

/// <summary>
/// Packs and unpacks with an external 7-Zip executable.
/// </summary>
/// <remarks>
/// Offered because LZMA2 beats deflate substantially on some titles, and a disc saved is a
/// disc saved. It is not the default: it puts a third-party binary on the critical path of an
/// install running from read-only media, which is exactly the dependency
/// <see cref="SdzArchiveEngine"/> exists to avoid. The Builder warns when a disc authored this
/// way will need 7-Zip present at install time.
/// </remarks>
public sealed partial class SevenZipArchiveEngine : IArchiveEngine
{
    private readonly IProcessRunner _runner;
    private readonly Lazy<string?> _executable;

    public SevenZipArchiveEngine(IProcessRunner? runner = null, string? executablePath = null)
    {
        _runner = runner ?? SystemProcessRunner.Instance;
        _executable = new Lazy<string?>(() => executablePath ?? FindExecutable());
    }

    public string FormatId => ArchiveFormats.SevenZip;

    public string VolumeExtension => "7z";

    public bool IsAvailable => _executable.Value is not null;

    /// <summary>Path of the executable in use, for diagnostics.</summary>
    public string? ExecutablePath => _executable.Value;

    public async Task<ArchiveCreateResult> CreateAsync(
        ArchiveCreateRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var executable = RequireExecutable();
        Directory.CreateDirectory(request.OutputDirectory);

        var tracker = new ProgressTracker(progress, OperationPhase.Compressing);
        var sourceSize = MeasureDirectory(request.SourceDirectory);
        tracker.SetTotals(sourceSize);

        var archivePath = Path.Combine(request.OutputDirectory, $"{request.BaseName}.{VolumeExtension}");

        var arguments = new List<string>
        {
            "a",
            "-t7z",
            "-y",
            "-bsp1", // progress to stdout
            $"-mx{ToLevel(request.Compression)}",
            $"-v{request.VolumeSize}b",
            archivePath,
            Path.Combine(request.SourceDirectory, "*"),
        };

        foreach (var exclude in request.ExcludeRelativePaths ?? Array.Empty<string>())
        {
            arguments.Add($"-xr!{exclude}");
        }

        var result = await _runner
            .RunAsync(new ProcessLaunch(executable, arguments, request.SourceDirectory), cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            throw new ArchiveIntegrityException(
                $"7-Zip failed with exit code {result.ExitCode}.{Environment.NewLine}{result.StandardError}");
        }

        tracker.Finish();

        var volumes = Directory
            .EnumerateFiles(request.OutputDirectory, $"{request.BaseName}.{VolumeExtension}.*")
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        if (volumes.Count == 0 && File.Exists(archivePath))
        {
            // 7-Zip omits the .001 suffix when everything fits in one volume.
            var single = archivePath + ".001";
            File.Move(archivePath, single);
            volumes.Add(single);
        }

        var compressed = volumes.Sum(p => new FileInfo(p).Length);
        return new ArchiveCreateResult(volumes, sourceSize, compressed, CountFiles(request.SourceDirectory));
    }

    public async Task<ArchiveExtractResult> ExtractAsync(
        ArchiveExtractRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var executable = RequireExecutable();

        if (request.Volumes is not FileVolumeSource fileSource)
        {
            // 7-Zip opens volumes itself by file name, so it cannot be fed a disc-swapping
            // source. Multi-disc sets must use the built-in format.
            throw new NotSupportedException(
                "The 7-Zip engine can only extract from volumes already present on disk. " +
                "Author multi-disc sets with the built-in 'sdz' format instead.");
        }

        Directory.CreateDirectory(request.DestinationDirectory);
        var tracker = new ProgressTracker(progress, OperationPhase.Extracting);

        var result = await _runner
            .RunAsync(
                new ProcessLaunch(
                    executable,
                    new[] { "x", "-y", "-bsp1", fileSource.Paths[0], $"-o{request.DestinationDirectory}" }),
                cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            throw new ArchiveIntegrityException(
                $"7-Zip extraction failed with exit code {result.ExitCode}.{Environment.NewLine}{result.StandardError}");
        }

        tracker.Finish();

        var extracted = MeasureDirectory(request.DestinationDirectory);
        return new ArchiveExtractResult(CountFiles(request.DestinationDirectory), extracted);
    }

    /// <summary>Parses a percentage out of a 7-Zip progress line such as " 42% 13 - file.pak".</summary>
    internal static int? ParseProgressPercent(string line)
    {
        var match = ProgressPattern().Match(line);
        return match.Success && int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var percent)
            ? percent
            : null;
    }

    [GeneratedRegex(@"(\d{1,3})%", RegexOptions.CultureInvariant)]
    private static partial Regex ProgressPattern();

    private static int ToLevel(ArchiveCompression compression) => compression switch
    {
        ArchiveCompression.Store => 0,
        ArchiveCompression.Fast => 1,
        ArchiveCompression.Balanced => 5,
        ArchiveCompression.Maximum => 9,
        _ => 1,
    };

    private string RequireExecutable()
        => _executable.Value ?? throw new InvalidOperationException(
            "No 7-Zip executable found. Install 7-Zip and ensure '7z' is on PATH.");

    private static string? FindExecutable()
    {
        // 7zz is the modern official CLI name; 7za is the standalone build.
        var names = OperatingSystem.IsWindows()
            ? new[] { "7z.exe", "7za.exe", "7zz.exe" }
            : new[] { "7z", "7zz", "7za" };

        var directories = new List<string>();
        if (Environment.GetEnvironmentVariable("PATH") is { Length: > 0 } path)
        {
            directories.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var variable in new[] { "ProgramFiles", "ProgramFiles(x86)" })
            {
                if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } programFiles)
                {
                    directories.Add(Path.Combine(programFiles, "7-Zip"));
                }
            }
        }
        else
        {
            directories.AddRange(new[] { "/usr/local/bin", "/opt/homebrew/bin", "/usr/bin" });
        }

        foreach (var directory in directories)
        {
            foreach (var name in names)
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(directory, name);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static long MeasureDirectory(string path)
        => Directory.Exists(path)
            ? new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
            : 0;

    private static int CountFiles(string path)
        => Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Count() : 0;
}
