using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SteamDisc.Core.Diagnostics;
using SteamDisc.Core.Progress;
using SteamDisc.Core.Protocol;
using SteamDisc.Core.Steam;
using SteamDisc.Core.Theming;

namespace SteamDisc.Install;

/// <param name="Library">The library written to.</param>
/// <param name="ManifestPath">The transplanted app manifest.</param>
/// <param name="Bytes">Size of the game in the target library.</param>
/// <param name="Files">Files copied or already present.</param>
/// <param name="SkippedFiles">Files already present and unchanged from an earlier run.</param>
/// <param name="Warnings">Manifest audit problems worth showing.</param>
public sealed record LibraryCopyResult(
    SteamLibrary Library,
    string ManifestPath,
    long Bytes,
    int Files,
    int SkippedFiles,
    IReadOnlyList<string> Warnings);

/// <param name="Succeeded">True when the action completed.</param>
/// <param name="Message">What to tell the user.</param>
public sealed record PortableOutcome(bool Succeeded, string Message);

/// <summary>
/// A portable library: a USB drive holding games as a raw Steam library rather than a
/// compressed payload. Such a drive can be played from directly once Steam is told about it,
/// or installed from with a plain copy — no extraction either way.
/// </summary>
/// <remarks>
/// Every copy follows the disc installer's ordering rule: files first, manifest last, so an
/// interrupted copy never leaves Steam an entry pointing at half a game. A re-run skips files
/// already present with the same size and timestamp, which makes an interrupted copy resumable.
/// </remarks>
public static class PortableLibrary
{
    /// <summary>
    /// Sequential read speed at which playing from the drive is recommended over installing —
    /// roughly a hard disk, which most games are still built to cope with.
    /// </summary>
    /// <remarks>
    /// ponytail: sequential only. Cheap flash is far worse at small random reads, which is what
    /// hurts open-world streaming; measure 4K random reads too if this recommends badly.
    /// </remarks>
    public const double PlayableMegabytesPerSecond = 100;

    /// <summary>FAT32's hard per-file limit: 4 GiB less one byte.</summary>
    public const long Fat32MaxFileSize = uint.MaxValue;

    /// <summary>Name the runtime takes on the drive root; running it offers play or install.</summary>
    public const string SetupExecutableName = "Setup.exe";

    /// <summary>Theme folder at the drive root, which skins the drive's Setup.exe like a disc's.</summary>
    public const string ThemeFolderName = "theme";

    private const int MegaByte = 1024 * 1024;

    /// <summary>
    /// Copies an installed app into another library and writes its transplanted manifest.
    /// Used both to write a game onto a USB drive and to install one from it.
    /// </summary>
    public static async Task<LibraryCopyResult> CopyAppAsync(
        InstalledApp source,
        SteamLibrary target,
        TransplantOptions transplant,
        IReadOnlyCollection<string>? excludeRelativePaths = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var installPath = target.InstallPath(source.Manifest.InstallDir);

        var excluded = new HashSet<string>(
            (excludeRelativePaths ?? Array.Empty<string>()).Select(p => p.Replace('\\', '/').Trim('/')),
            StringComparer.OrdinalIgnoreCase);

        var files = new DirectoryInfo(source.InstallPath)
            .EnumerateFiles("*", SearchOption.AllDirectories)
            .Select(f => (Source: f, Relative: Path.GetRelativePath(source.InstallPath, f.FullName).Replace('\\', '/')))
            .Where(f => !IsExcluded(f.Relative, excluded))
            .Select(f => new CopyItem(f.Source, Path.Combine(installPath, f.Relative), f.Relative))
            .ToList();

        var total = files.Sum(f => f.Source.Length);

        if (TryGetDriveFormat(target.Path) is "FAT32" or "FAT"
            && files.FirstOrDefault(f => f.Source.Length > Fat32MaxFileSize) is { } tooBig)
        {
            throw new InvalidOperationException(
                $"'{target.Path}' is formatted FAT32, which cannot hold '{tooBig.Relative}' " +
                $"({InstallEngine.FormatBytes(tooBig.Source.Length)}). Reformat the drive as NTFS.");
        }

        var needed = files.Where(f => !f.IsUpToDate).Sum(f => f.Source.Length);
        if (target.GetAvailableFreeBytes() is { } free && free < needed)
        {
            throw new InvalidOperationException(
                $"Not enough space on '{target.Path}': {InstallEngine.FormatBytes(free)} free, " +
                $"{InstallEngine.FormatBytes(needed)} needed.");
        }

        // An old manifest must not outlive a half-finished copy: Steam would "repair" by downloading.
        var manifestPath = target.ManifestPath(source.AppId);
        Directory.CreateDirectory(target.SteamAppsPath);
        File.Delete(manifestPath);

        var tracker = new ProgressTracker(progress, OperationPhase.Extracting);
        tracker.SetTotals(total, files.Count);

        var skipped = 0;
        var buffer = new byte[MegaByte];

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            tracker.SetCurrentItem(file.Relative);

            if (file.IsUpToDate)
            {
                skipped++;
                tracker.CompleteItem(file.Source.Length);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(file.Destination)!);

            await using (var input = file.Source.OpenRead())
            await using (var output = new FileStream(file.Destination, FileMode.Create, FileAccess.Write, FileShare.None, 1))
            {
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    tracker.AddBytes(read);
                }
            }

            // Matching timestamps are what lets a re-run skip this file.
            File.SetLastWriteTimeUtc(file.Destination, file.Source.LastWriteTimeUtc);
            tracker.CompleteItem();
        }

        tracker.SetPhase(OperationPhase.WritingManifest);

        var prepared = AppManifestTransplant.Prepare(source.Manifest, transplant with { SizeOnDisk = total });
        prepared.InstallDir = source.Manifest.InstallDir;

        var warnings = AppManifestTransplant.Audit(prepared).Select(p => "App manifest: " + p).ToList();
        prepared.Save(manifestPath);

        tracker.Finish();
        return new LibraryCopyResult(target, manifestPath, total, files.Count, skipped, warnings);
    }

    /// <summary>
    /// Writes a game onto a drive as a portable library, with the runtime alongside so the
    /// drive offers play-or-install wherever it is plugged in, skinned by <paramref name="theme"/>.
    /// </summary>
    /// <remarks>
    /// A drive has one Setup.exe but may hold several games, so the theme is the last game
    /// written's — the same "last one wins" as the drive's label would be.
    /// </remarks>
    public static async Task<LibraryCopyResult> WriteAsync(
        InstalledApp game,
        string driveRoot,
        IReadOnlyCollection<string>? excludeRelativePaths = null,
        string? runtimeExecutablePath = null,
        ThemeDefinition? theme = null,
        IReadOnlyDictionary<string, string>? artwork = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var library = new SteamLibrary(driveRoot);
        library.EnsureLibraryFolderFile();

        // Owner stays the authoring account and no launcher path is written: the drive has no
        // single "local" machine. Updates wait for a launch rather than starting the moment the
        // drive is plugged into some PC.
        var result = await CopyAppAsync(
                game,
                library,
                new TransplantOptions(AutoUpdate: AutoUpdateBehavior.OnlyOnLaunch),
                excludeRelativePaths,
                progress,
                cancellationToken)
            .ConfigureAwait(false);

        if (runtimeExecutablePath is { Length: > 0 } runtime && File.Exists(runtime))
        {
            File.Copy(runtime, Path.Combine(library.Path, SetupExecutableName), overwrite: true);
        }

        if (theme is not null)
        {
            // Start clean: art left over from the previous game would otherwise satisfy this
            // theme's asset names and skin the drive with the wrong game.
            var themeFolder = Path.Combine(library.Path, ThemeFolderName);
            if (Directory.Exists(themeFolder))
            {
                Directory.Delete(themeFolder, recursive: true);
            }

            BuiltInThemes.WriteThemeFolder(theme, themeFolder, artwork);
        }

        return result;
    }

    /// <summary>
    /// Measures the drive's sequential read speed in MB/s by reading part of its largest game
    /// file, bypassing the OS cache. Null when there is nothing big enough to measure.
    /// </summary>
    public static double? MeasureReadSpeed(SteamLibrary library, long sampleBytes = 256L * MegaByte)
    {
        const int block = 4 * MegaByte;

        // FILE_FLAG_NO_BUFFERING: on the machine that just wrote the drive, a cached read would
        // report RAM speed rather than the stick's.
        const FileOptions noBuffering = (FileOptions)0x20000000;

        try
        {
            var largest = new DirectoryInfo(library.CommonPath)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .MaxBy(f => f.Length);

            var limit = Math.Min(largest?.Length ?? 0, sampleBytes) / block * block;
            if (largest is null || limit == 0)
            {
                return null;
            }

            // Unbuffered reads need a sector-aligned buffer; 4 KiB alignment covers every drive.
            var raw = GC.AllocateUninitializedArray<byte>(block + 4096, pinned: true);
            var misalignment = (int)(Marshal.UnsafeAddrOfPinnedArrayElement(raw, 0) % 4096);
            var span = raw.AsSpan(misalignment == 0 ? 0 : 4096 - misalignment, block);

            using SafeFileHandle handle = File.OpenHandle(
                largest.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                OperatingSystem.IsWindows() ? noBuffering : FileOptions.None);

            var clock = Stopwatch.StartNew();
            long read = 0;
            while (read < limit)
            {
                var n = RandomAccess.Read(handle, span, read);
                if (n <= 0)
                {
                    break;
                }

                read += n;
            }

            return read / (double)MegaByte / clock.Elapsed.TotalSeconds;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when this drive was set up to play from on this PC but Steam no longer lists it at
    /// its current path: Steam dropped the library, or the drive came back under a new letter.
    /// Setup then re-adds it straight away instead of asking play-or-install again.
    /// </summary>
    public static bool NeedsReRegistration(SteamInstallation steam, SteamLibrary library)
        => !IsRegistered(steam, library) && PlayedHere().Contains(library.EnsureLibraryFolderFile());

    private static bool IsRegistered(SteamInstallation steam, SteamLibrary library)
        => steam.GetLibraries().Any(l => SteamInstallation.PathComparer.Equals(
            Path.TrimEndingDirectorySeparator(l.Path), Path.TrimEndingDirectorySeparator(library.Path)));

    /// <summary>
    /// Content ids of drives chosen for play on this PC. Kept on the PC, not the drive, because
    /// "played here" is a fact about this machine's Steam.
    /// </summary>
    private static string PlayedHerePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamDisc", "played-drives.txt");

    private static HashSet<string> PlayedHere()
    {
        try
        {
            return new HashSet<string>(File.ReadAllLines(PlayedHerePath), StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private static void RememberPlayedHere(string contentId)
    {
        try
        {
            if (!PlayedHere().Contains(contentId))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PlayedHerePath)!);
                File.AppendAllLines(PlayedHerePath, new[] { contentId });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only costs the auto re-register shortcut; the drive itself is registered.
        }
    }

    /// <summary>Adds the drive to this machine's Steam so its games play from it.</summary>
    public static async Task<PortableOutcome> RegisterAsync(
        SteamInstallation steam,
        SteamLibrary library,
        IInstallHost host,
        ISteamDiscLogger? logger = null,
        IProcessRunner? processRunner = null,
        CancellationToken cancellationToken = default)
    {
        var contentId = library.EnsureLibraryFolderFile();
        if (IsRegistered(steam, library))
        {
            RememberPlayedHere(contentId);
            return new PortableOutcome(true, $"Steam already uses '{library.Path}'. Its games are ready to play.");
        }

        var returning = PlayedHere().Contains(contentId);

        // Steam overwrites libraryfolders.vdf from memory on exit, so it has to be closed around the edit.
        var cli = new SteamCliDriver(steam, processRunner);
        if (!await CloseSteamAsync(
                cli,
                host,
                "Steam has to close briefly so this drive can be added to its libraries. Close Steam now?",
                cancellationToken).ConfigureAwait(false))
        {
            return new PortableOutcome(false, "Steam is still running, so the drive was not added.");
        }

        steam.RegisterLibrary(library.Path, contentId);
        RememberPlayedHere(contentId);
        logger?.Info($"Registered portable library '{library.Path}'.");
        StartSteam(cli);

        return new PortableOutcome(
            true,
            returning
                ? $"Steam had lost track of this drive, so it was added back at '{library.Path}'. Its games are ready to play."
                : $"Added '{library.Path}' to Steam. Its games will appear in your library and play from the drive. " +
                  "Keep the drive on the same letter; Steam finds the library by path.");
    }

    /// <summary>Copies every game on the drive into a library on this machine.</summary>
    public static async Task<PortableOutcome> InstallAsync(
        SteamInstallation steam,
        SteamLibrary portable,
        SteamLibrary target,
        IInstallHost host,
        IProgress<OperationProgress>? progress = null,
        bool validate = false,
        ISteamDiscLogger? logger = null,
        IProcessRunner? processRunner = null,
        CancellationToken cancellationToken = default)
    {
        var apps = portable.GetInstalledApps();
        if (apps.Count == 0)
        {
            return new PortableOutcome(false, $"No games were found on '{portable.Path}'.");
        }

        // Local install: the manifest names this machine's account and client, as the disc installer does.
        var transplant = new TransplantOptions(
            LocalSteamId: steam.GetMostRecentUser()?.SteamId64,
            LauncherPath: steam.ClientExecutablePath,
            RequestValidation: validate);

        foreach (var app in apps)
        {
            logger?.Info($"Installing {app} from '{portable.Path}' to '{target.Path}'.");
            var result = await CopyAppAsync(app, target, transplant, null, progress, cancellationToken)
                .ConfigureAwait(false);

            foreach (var warning in result.Warnings)
            {
                host.ReportWarning(warning);
            }
        }

        // A running client does not notice manifests written behind its back until it restarts.
        var cli = new SteamCliDriver(steam, processRunner);
        var restarted = await CloseSteamAsync(
                cli,
                host,
                "Steam needs to restart before it will see the installed game. Restart it now?",
                cancellationToken)
            .ConfigureAwait(false);
        StartSteam(cli);

        var names = string.Join(", ", apps.Select(a => a.Manifest.Name));
        return new PortableOutcome(
            true,
            $"Installed {names} to '{target.Path}'." +
            (restarted ? string.Empty : " Restart Steam to see it."));
    }

    /// <summary>True when Steam is (now) closed; asks first if it was running.</summary>
    private static async Task<bool> CloseSteamAsync(
        SteamCliDriver cli,
        IInstallHost host,
        string question,
        CancellationToken cancellationToken)
    {
        if (!SteamClientState.IsRunning())
        {
            return true;
        }

        if (!cli.IsAvailable || !await host.ConfirmAsync(question, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await cli.ShutdownAsync(cancellationToken).ConfigureAwait(false);

        // steamwebhelper lingers after the main process; editing before it is gone loses the edit.
        for (var i = 0; i < 30 && SteamClientState.IsRunning(); i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }

        return !SteamClientState.IsRunning();
    }

    private static void StartSteam(SteamCliDriver cli)
    {
        if (cli.IsAvailable && !SteamClientState.IsRunning())
        {
            cli.Start();
        }
    }

    private static bool IsExcluded(string relativePath, HashSet<string> excluded)
        => excluded.Contains(relativePath)
           || excluded.Any(e => relativePath.StartsWith(e + "/", StringComparison.OrdinalIgnoreCase));

    private static string? TryGetDriveFormat(string path)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(path)!).DriveFormat;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed record CopyItem(FileInfo Source, string Destination, string Relative)
    {
        public bool IsUpToDate { get; } =
            File.Exists(Destination)
            && new FileInfo(Destination) is var existing
            && existing.Length == Source.Length
            && existing.LastWriteTimeUtc == Source.LastWriteTimeUtc;
    }
}
