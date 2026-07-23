using System.Diagnostics;
using SteamDisc.Core.Archive;
using SteamDisc.Core.Diagnostics;
using SteamDisc.Core.Hashing;
using SteamDisc.Core.Payload;
using SteamDisc.Core.Progress;
using SteamDisc.Core.Protocol;
using SteamDisc.Core.Steam;

namespace SteamDisc.Install;

/// <summary>
/// Installs a payload into a Steam library and registers it with the client — the Path B
/// mechanism from the project plan, end to end.
/// </summary>
/// <remarks>
/// Ordering matters and is deliberate: files land first, the app manifest is written last.
/// If anything fails partway, Steam has never been told the game exists, so the user is left
/// with a stray folder rather than a library entry pointing at an incomplete install — which
/// Steam would "repair" by downloading the whole thing.
/// </remarks>
public sealed class InstallEngine
{
    private readonly IInstallHost _host;
    private readonly ISteamDiscLogger _logger;
    private readonly ArchiveEngineRegistry _archives;
    private readonly IProcessRunner _processRunner;

    public InstallEngine(
        IInstallHost host,
        ISteamDiscLogger? logger = null,
        ArchiveEngineRegistry? archives = null,
        IProcessRunner? processRunner = null)
    {
        _host = host;
        _logger = logger ?? NullLogger.Instance;
        _archives = archives ?? ArchiveEngineRegistry.CreateDefault();
        _processRunner = processRunner ?? SystemProcessRunner.Instance;
    }

    /// <summary>
    /// Checks everything that can be checked before writing anything, so the user learns about
    /// a full disk or a missing volume up front rather than 20 minutes in.
    /// </summary>
    public InstallPreflight Preflight(InstallRequest request)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        errors.AddRange(request.Manifest.Validate());

        var installPath = request.TargetLibrary.InstallPath(request.Manifest.InstallDir);

        if (!Directory.Exists(request.TargetLibrary.SteamAppsPath))
        {
            errors.Add($"'{request.TargetLibrary.Path}' does not look like a Steam library.");
        }

        var required = request.Manifest.SizeOnDisk;
        var free = request.TargetLibrary.GetAvailableFreeBytes();
        if (free is { } available && required > 0)
        {
            // Ask for a little headroom: Steam writes its own bookkeeping alongside the game.
            var needed = required + (256L * 1024 * 1024);
            if (available < needed)
            {
                errors.Add(
                    $"Not enough space in '{request.TargetLibrary.Path}': " +
                    $"{FormatBytes(available)} free, {FormatBytes(needed)} needed.");
            }
        }

        if (Directory.Exists(installPath) && Directory.EnumerateFileSystemEntries(installPath).Any())
        {
            warnings.Add($"'{installPath}' already exists and will be overwritten.");
        }

        var existing = request.TargetLibrary.FindApp(request.Manifest.AppId);
        if (existing is not null)
        {
            warnings.Add(
                $"Steam already has {existing.Manifest.Name} registered in this library; " +
                "its manifest will be replaced.");
        }

        // Volumes on the disc currently in the drive must actually be there. Volumes on later
        // discs are checked when they are asked for.
        foreach (var volume in request.Manifest.Archive.Volumes.Where(v => v.Disc == request.Manifest.Disc.Number))
        {
            var path = Path.Combine(request.DiscRoot, volume.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                errors.Add($"Volume {volume.Index} is missing from this disc ({volume.Path}).");
            }
        }

        if (request.Manifest.Archive.Format == ArchiveFormats.SevenZip &&
            _archives.Find(ArchiveFormats.SevenZip)?.IsAvailable != true)
        {
            errors.Add("This disc was authored with 7-Zip compression, but no 7z executable was found.");
        }

        foreach (var advisory in request.Manifest.Advisories)
        {
            warnings.Add(advisory.Message);
        }

        return new InstallPreflight(errors, warnings, installPath, required, free);
    }

    public async Task<InstallResult> InstallAsync(
        InstallRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        var warnings = new List<string>();
        string? installPath = null;

        try
        {
            var preflight = Preflight(request);
            if (preflight.Errors.Count > 0)
            {
                throw new InvalidOperationException(
                    "Cannot install:" + Environment.NewLine +
                    string.Join(Environment.NewLine, preflight.Errors.Select(e => "  " + e)));
            }

            warnings.AddRange(preflight.Warnings);
            installPath = preflight.InstallPath;

            _logger.Info($"Installing {request.Manifest.Title} ({request.Manifest.AppId}) to '{installPath}'.");

            if (request.VerifyVolumesUpFront)
            {
                await VerifyVolumesAsync(request, progress, cancellationToken).ConfigureAwait(false);
            }

            var extractResult = await ExtractAsync(request, installPath, progress, cancellationToken)
                .ConfigureAwait(false);

            if (request.RunPrerequisites && request.Manifest.Prerequisites.Count > 0)
            {
                var tracker = new ProgressTracker(progress, OperationPhase.RunningPrerequisites);
                tracker.SetPhase(OperationPhase.RunningPrerequisites);

                var failures = await new PrerequisiteRunner(_processRunner, _logger)
                    .RunAsync(request.Manifest.Prerequisites, installPath, cancellationToken)
                    .ConfigureAwait(false);

                foreach (var failure in failures)
                {
                    var message = $"A prerequisite did not install cleanly — {failure}";
                    warnings.Add(message);
                    _host.ReportWarning(message);
                    _logger.Warn(message);
                }
            }

            var manifestPath = WriteAppManifest(request, installPath, extractResult.UncompressedBytes, warnings);

            await FinishAsync(request, warnings, cancellationToken).ConfigureAwait(false);

            clock.Stop();
            _logger.Info($"Install finished in {clock.Elapsed:hh\\:mm\\:ss}.");

            return new InstallResult(
                InstallOutcome.Succeeded,
                installPath,
                manifestPath,
                extractResult.UncompressedBytes,
                clock.Elapsed,
                warnings);
        }
        catch (OperationCanceledException)
        {
            _logger.Warn("Install cancelled.");
            return new InstallResult(InstallOutcome.Cancelled, installPath, null, 0, clock.Elapsed, warnings);
        }
        catch (Exception ex)
        {
            _logger.Error("Install failed.", ex);
            return new InstallResult(InstallOutcome.Failed, installPath, null, 0, clock.Elapsed, warnings, ex);
        }
    }

    private async Task<ArchiveExtractResult> ExtractAsync(
        InstallRequest request,
        string installPath,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var engine = _archives.Require(request.Manifest.Archive.Format);

        IVolumeSource volumes = request.Manifest.Disc.IsSingleDisc
            ? new FileVolumeSource(request.Manifest.Archive.Volumes
                .Select(v => Path.Combine(request.DiscRoot, v.Path.Replace('/', Path.DirectorySeparatorChar)))
                .ToList())
            : new DiscSetVolumeSource(request.Manifest, request.DiscRoot, _host, _logger);

        Directory.CreateDirectory(installPath);

        return await engine
            .ExtractAsync(
                new ArchiveExtractRequest(volumes, installPath, request.VerifyHashes),
                progress,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task VerifyVolumesAsync(
        InstallRequest request,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var tracker = new ProgressTracker(progress, OperationPhase.Verifying);
        var onThisDisc = request.Manifest.Archive.Volumes
            .Where(v => v.Disc == request.Manifest.Disc.Number && !string.IsNullOrEmpty(v.Sha256))
            .ToList();

        tracker.SetTotals(onThisDisc.Sum(v => v.Size), onThisDisc.Count);

        foreach (var volume in onThisDisc)
        {
            cancellationToken.ThrowIfCancellationRequested();
            tracker.SetCurrentItem(volume.Path);

            var path = Path.Combine(request.DiscRoot, volume.Path.Replace('/', Path.DirectorySeparatorChar));
            var actual = await Sha256File
                .ComputeAsync(path, bytes => tracker.AddBytes(bytes), cancellationToken)
                .ConfigureAwait(false);

            if (!string.Equals(actual, volume.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArchiveIntegrityException(
                    $"Volume {volume.Index} ({volume.Path}) failed verification. The disc is damaged or was modified.");
            }

            tracker.CompleteItem();
        }

        tracker.Finish();
    }

    /// <summary>
    /// Writes the app manifest. Done last, and only once the files are known to be in place.
    /// </summary>
    private string WriteAppManifest(
        InstallRequest request,
        string installPath,
        long extractedBytes,
        List<string> warnings)
    {
        var sourcePath = Path.Combine(
            request.DiscRoot,
            request.Manifest.AppManifestPath.Replace('/', Path.DirectorySeparatorChar));

        AppManifest source;
        if (File.Exists(sourcePath))
        {
            source = AppManifest.Load(sourcePath);
        }
        else
        {
            // No transplanted manifest: synthesise one and be honest that Steam will very
            // likely decide it needs to re-download.
            warnings.Add(
                "This disc carries no captured app manifest, so a minimal one was synthesised. " +
                "Steam is likely to re-download the game.");
            source = AppManifest.CreateMinimal(
                request.Manifest.AppId, request.Manifest.Title, request.Manifest.InstallDir);
            source.BuildId = request.Manifest.BuildId;
        }

        var localUser = request.Steam.GetMostRecentUser();
        var validate = request.ValidateAfterInstall ?? request.Manifest.PostInstall.Validate;

        var prepared = AppManifestTransplant.Prepare(source, new TransplantOptions(
            LocalSteamId: localUser?.SteamId64,
            LauncherPath: request.Steam.ClientExecutablePath,
            RequestValidation: validate,
            AutoUpdate: request.AutoUpdate,
            SizeOnDisk: extractedBytes > 0 ? extractedBytes : null));

        // The manifest must describe where the files actually went.
        prepared.InstallDir = request.Manifest.InstallDir;

        foreach (var problem in AppManifestTransplant.Audit(prepared))
        {
            var message = "App manifest: " + problem;
            warnings.Add(message);
            _logger.Warn(message);
        }

        if (localUser is null)
        {
            warnings.Add(
                "No signed-in Steam account was found on this machine, so the manifest kept the " +
                "authoring account as its owner. Sign in to Steam and re-run if the game does not appear.");
        }

        var targetPath = request.TargetLibrary.ManifestPath(request.Manifest.AppId);
        prepared.Save(targetPath);
        _logger.Info($"Wrote '{targetPath}'.");

        _ = installPath;
        return targetPath;
    }

    private async Task FinishAsync(InstallRequest request, List<string> warnings, CancellationToken cancellationToken)
    {
        var protocol = new SteamProtocolDriver(_processRunner);
        var cli = new SteamCliDriver(request.Steam, _processRunner);

        var validate = request.ValidateAfterInstall ?? request.Manifest.PostInstall.Validate;
        var launch = request.Launch ?? request.Manifest.PostInstall.Launch;

        // A running client caches the library state it read at startup, so a manifest we drop
        // in behind its back may go unnoticed until it restarts. Ask rather than assume —
        // killing a client mid-download of something else would be rude.
        if (SteamClientState.IsRunning() && request.Manifest.PostInstall.RestartSteam)
        {
            var restart = await _host
                .ConfirmAsync(
                    "Steam needs to restart before it will see the installed game. Restart it now?",
                    cancellationToken)
                .ConfigureAwait(false);

            if (restart)
            {
                _logger.Info("Restarting Steam.");
                await cli.ShutdownAsync(cancellationToken).ConfigureAwait(false);

                // The client takes a moment to release its files; starting too early
                // produces a second process that immediately exits.
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                cli.Start();
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                warnings.Add("Steam was not restarted; the game may not appear until it is.");
            }
        }

        if (validate)
        {
            _logger.Info("Asking Steam to verify the install.");
            protocol.Validate(request.Manifest.AppId);
            return;
        }

        if (launch)
        {
            _logger.Info("Launching the game.");
            protocol.Launch(request.Manifest.AppId);
        }
    }

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

        return $"{value:0.##} {units[unit]}";
    }
}

/// <param name="Errors">Reasons the install cannot proceed.</param>
/// <param name="Warnings">Things the user should see first but that do not block.</param>
/// <param name="InstallPath">Where the game will be written.</param>
/// <param name="RequiredBytes">Uncompressed size needed.</param>
/// <param name="AvailableBytes">Free space on the target volume, when known.</param>
public sealed record InstallPreflight(
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    string InstallPath,
    long RequiredBytes,
    long? AvailableBytes)
{
    public bool CanProceed => Errors.Count == 0;
}
