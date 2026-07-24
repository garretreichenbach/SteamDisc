using System.Diagnostics;
using SteamDisc.Core.Archive;
using SteamDisc.Core.Diagnostics;
using SteamDisc.Core.Hashing;
using SteamDisc.Core.Payload;
using SteamDisc.Core.Progress;
using SteamDisc.Core.Theming;
using SteamDisc.Imaging;

namespace SteamDisc.Authoring;

/// <param name="Game">The game to package.</param>
/// <param name="OutputDirectory">Staging root. One subfolder per disc is created beneath it.</param>
/// <param name="Medium">Target media, which drives volume sizing and disc spanning.</param>
public sealed record PackageRequest(GameCandidate Game, string OutputDirectory, OpticalMedium Medium)
{
    public ArchiveCompression Compression { get; init; } = ArchiveCompression.Fast;

    public string ArchiveFormat { get; init; } = ArchiveFormats.Sdz;

    /// <summary>Override the computed volume size. 0 means "choose one".</summary>
    public long VolumeSize { get; init; }

    /// <summary>Theme definition to write into each disc's <c>theme/</c> folder.</summary>
    public ThemeDefinition? Theme { get; init; }

    /// <summary>Artwork to copy into the theme, keyed by slot ("background", "logo", "cover").</summary>
    public IReadOnlyDictionary<string, string>? Artwork { get; init; }

    /// <summary>Path to a published runtime executable to copy in as <c>Setup.exe</c>.</summary>
    public string? RuntimeExecutablePath { get; init; }

    /// <summary>Compute a SHA-256 sidecar for the volumes. Costs a second full read.</summary>
    public bool WriteHashSidecar { get; init; } = true;

    public bool Validate { get; init; }

    public bool LaunchAfterInstall { get; init; } = true;

    /// <summary>Relative paths inside the game folder to leave out of the archive.</summary>
    public IReadOnlyCollection<string>? ExcludeRelativePaths { get; init; }

    /// <summary>Optional version label for the game, shown on the installer, e.g. "v1.2".</summary>
    public string? VersionLabel { get; init; }
}

/// <param name="DiscRoots">Staging folder for each disc, in order.</param>
/// <param name="Manifest">The manifest written to disc 1.</param>
/// <param name="Plan">How volumes were spread across discs.</param>
/// <param name="UncompressedBytes">Size of the source game folder.</param>
/// <param name="CompressedBytes">Total size of the payload volumes.</param>
/// <param name="Duration">Wall-clock time.</param>
public sealed record PackageResult(
    IReadOnlyList<string> DiscRoots,
    PayloadManifest Manifest,
    DiscSpanPlan Plan,
    long UncompressedBytes,
    long CompressedBytes,
    TimeSpan Duration)
{
    public double CompressionRatio => UncompressedBytes > 0 ? (double)CompressedBytes / UncompressedBytes : 1d;
}

/// <summary>
/// Turns an installed game into a set of disc staging folders — the authoring half of the
/// project, and everything M3 needs.
/// </summary>
/// <remarks>
/// The output is a plain folder tree rather than an image, for two reasons: it can be
/// installed from directly (which is how the runtime gets tested without burning anything),
/// and it can be inspected and hand-edited before it becomes a coaster.
/// </remarks>
public sealed class PackageBuilder
{
    private readonly ArchiveEngineRegistry _archives;
    private readonly ISteamDiscLogger _logger;

    public PackageBuilder(ArchiveEngineRegistry? archives = null, ISteamDiscLogger? logger = null)
    {
        _archives = archives ?? ArchiveEngineRegistry.CreateDefault();
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task<PackageResult> BuildAsync(
        PackageRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        var engine = _archives.Require(request.ArchiveFormat);
        var game = request.Game;

        if (!Directory.Exists(game.InstallPath))
        {
            throw new DirectoryNotFoundException($"'{game.InstallPath}' does not exist.");
        }

        Directory.CreateDirectory(request.OutputDirectory);

        // Pack into a scratch folder first; volumes are only distributed to disc folders once
        // their sizes are known, since that is what the spanning plan needs.
        var stagingArchive = Path.Combine(request.OutputDirectory, ".payload");
        if (Directory.Exists(stagingArchive))
        {
            Directory.Delete(stagingArchive, recursive: true);
        }

        Directory.CreateDirectory(stagingArchive);

        var volumeSize = DiscSpanPlanner.ChooseVolumeSize(request.Medium, request.VolumeSize);
        _logger.Info($"Packing '{game.Name}' with {volumeSize:N0} byte volumes ({engine.FormatId}).");

        var archiveResult = await engine
            .CreateAsync(
                new ArchiveCreateRequest(
                    game.InstallPath,
                    stagingArchive,
                    "payload",
                    volumeSize,
                    request.Compression,
                    request.ExcludeRelativePaths),
                progress,
                cancellationToken)
            .ConfigureAwait(false);

        var volumes = new List<VolumeDescriptor>();
        for (var i = 0; i < archiveResult.VolumePaths.Count; i++)
        {
            var path = archiveResult.VolumePaths[i];
            volumes.Add(new VolumeDescriptor
            {
                Index = i + 1,
                Path = "data/" + Path.GetFileName(path),
                Size = new FileInfo(path).Length,
            });
        }

        var plan = DiscSpanPlanner.Assign(volumes, request.Medium);
        _logger.Info($"Payload spans {plan.DiscCount} disc(s) of {request.Medium.Name}.");

        if (request.WriteHashSidecar)
        {
            await ComputeVolumeHashesAsync(archiveResult.VolumePaths, volumes, progress, cancellationToken)
                .ConfigureAwait(false);
        }

        var setId = Guid.NewGuid().ToString("D");
        var discRoots = new List<string>();

        for (var disc = 1; disc <= plan.DiscCount; disc++)
        {
            var discRoot = plan.DiscCount == 1
                ? Path.Combine(request.OutputDirectory, "disc")
                : Path.Combine(request.OutputDirectory, $"disc{disc}");

            Directory.CreateDirectory(discRoot);
            discRoots.Add(discRoot);

            var manifest = BuildManifest(request, archiveResult, volumes, plan, setId, disc);
            WriteDisc(request, discRoot, manifest, volumes, archiveResult.VolumePaths, disc);
        }

        Directory.Delete(stagingArchive, recursive: true);
        clock.Stop();

        var firstManifest = PayloadManifest.Load(Path.Combine(discRoots[0], PayloadManifest.FileName));

        _logger.Info(
            $"Packaged in {clock.Elapsed:hh\\:mm\\:ss}: " +
            $"{archiveResult.UncompressedBytes:N0} -> {archiveResult.CompressedBytes:N0} bytes.");

        return new PackageResult(
            discRoots,
            firstManifest,
            plan,
            archiveResult.UncompressedBytes,
            archiveResult.CompressedBytes,
            clock.Elapsed);
    }

    private static async Task ComputeVolumeHashesAsync(
        IReadOnlyList<string> volumePaths,
        List<VolumeDescriptor> volumes,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var tracker = new ProgressTracker(progress, OperationPhase.Hashing);
        tracker.SetTotals(volumes.Sum(v => v.Size), volumes.Count);

        for (var i = 0; i < volumePaths.Count; i++)
        {
            tracker.SetCurrentItem(Path.GetFileName(volumePaths[i]));
            volumes[i].Sha256 = await Sha256File
                .ComputeAsync(volumePaths[i], bytes => tracker.AddBytes(bytes), cancellationToken)
                .ConfigureAwait(false);
            tracker.CompleteItem();
        }

        tracker.Finish();
    }

    private static PayloadManifest BuildManifest(
        PackageRequest request,
        ArchiveCreateResult archive,
        IReadOnlyList<VolumeDescriptor> volumes,
        DiscSpanPlan plan,
        string setId,
        int discNumber)
    {
        var game = request.Game;

        return new PayloadManifest
        {
            Title = game.Name,
            AppId = game.AppId,
            InstallDir = game.InstallDir,
            BuildId = game.BuildId,
            Version = string.IsNullOrWhiteSpace(request.VersionLabel) ? null : request.VersionLabel.Trim(),
            SizeOnDisk = archive.UncompressedBytes,
            FileCount = archive.FileCount,
            CreatedUtc = DateTimeOffset.UtcNow,
            CreatedBy = $"SteamDisc {typeof(PackageBuilder).Assembly.GetName().Version}",
            Disc = new DiscDescriptor
            {
                Number = discNumber,
                Of = plan.DiscCount,
                SetId = setId,
                Label = plan.DiscCount == 1 ? game.Name : $"{game.Name} — Disc {discNumber} of {plan.DiscCount}",
            },
            Archive = new ArchiveDescriptor
            {
                Format = request.ArchiveFormat,
                BaseName = "data/payload",
                // Every disc carries the full volume list: the runtime needs to know which
                // disc holds volume N before it has ever seen that disc.
                Volumes = volumes.ToList(),
                HashFile = request.WriteHashSidecar ? "data/payload.sha256" : null,
                CompressedSize = archive.CompressedBytes,
            },
            AppManifestPath = Core.Steam.AppManifest.FileNameFor(game.AppId),
            ThemePath = "theme",
            Prerequisites = PrerequisiteScanner.Scan(game.InstallPath).ToList(),
            PostInstall = new PostInstallOptions
            {
                Validate = request.Validate,
                Launch = request.LaunchAfterInstall,
                RestartSteam = true,
            },
            Advisories = game.Advisories.ToList(),
        };
    }

    private void WriteDisc(
        PackageRequest request,
        string discRoot,
        PayloadManifest manifest,
        IReadOnlyList<VolumeDescriptor> volumes,
        IReadOnlyList<string> volumePaths,
        int discNumber)
    {
        var dataDirectory = Path.Combine(discRoot, "data");
        Directory.CreateDirectory(dataDirectory);

        // Move only this disc's volumes; a copy would double the disk space needed.
        var onThisDisc = volumes.Where(v => v.Disc == discNumber).ToList();
        foreach (var volume in onThisDisc)
        {
            var source = volumePaths[volume.Index - 1];
            var destination = Path.Combine(discRoot, volume.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            if (File.Exists(destination))
            {
                File.Delete(destination);
            }

            File.Move(source, destination);
        }

        if (manifest.Archive.HashFile is { } hashFile)
        {
            Sha256File.Write(
                Path.Combine(discRoot, hashFile.Replace('/', Path.DirectorySeparatorChar)),
                onThisDisc
                    .Where(v => v.Sha256 is not null)
                    .Select(v => new Sha256Entry(v.Sha256!, Path.GetFileName(v.Path))));
        }

        // The captured app manifest — the transplant the whole Path B approach depends on.
        var sourceManifest = request.Game.App.ManifestPath;
        if (File.Exists(sourceManifest))
        {
            File.Copy(sourceManifest, Path.Combine(discRoot, manifest.AppManifestPath), overwrite: true);
        }
        else
        {
            _logger.Warn($"No app manifest found at '{sourceManifest}'; the disc will have to synthesise one.");
            manifest.AppManifestPath = string.Empty;
        }

        var theme = request.Theme ?? BuiltInThemes.ValveRetail2011();
        BuiltInThemes.WriteThemeFolder(theme, Path.Combine(discRoot, "theme"), request.Artwork);

        var hasRuntime = false;
        if (request.RuntimeExecutablePath is { Length: > 0 } runtime && File.Exists(runtime))
        {
            File.Copy(runtime, Path.Combine(discRoot, "Setup.exe"), overwrite: true);
            hasRuntime = true;
        }
        else if (discNumber == 1)
        {
            _logger.Warn(
                "No runtime executable was supplied, so the disc has no Setup.exe. " +
                "Publish SteamDisc.Runtime and pass its path to make the disc self-contained.");
        }

        // Show the SteamDisc icon on the mounted disc by borrowing the one baked into Setup.exe.
        AutorunFile.Write(
            discRoot,
            manifest.Disc.Label ?? manifest.Title,
            iconPath: hasRuntime ? "Setup.exe" : null);
        manifest.Save(Path.Combine(discRoot, PayloadManifest.FileName));
    }
}
