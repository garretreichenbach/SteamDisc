using System.Globalization;
using SteamDisc.Core.Diagnostics;
using SteamDisc.Core.Payload;
using SteamDisc.Core.Steam;
using SteamDisc.Core.Theming;
using SteamDisc.Install;
using SteamDisc.Runtime;

// The disc runtime: this is what ships as Setup.exe in the disc root.
//
// It reads payload.json from the folder it lives in, works out where Steam is and which
// library to install into, extracts, registers the app manifest, and hands off to Steam.
// The console front-end is both the fallback and the test harness; a skinned front-end sits
// on the same InstallEngine and implements the same IInstallHost.

return await RuntimeProgram.RunAsync(args).ConfigureAwait(false);

internal static class RuntimeProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        var options = RuntimeOptions.Parse(args);

        if (options.ShowHelp)
        {
            PrintUsage();
            return 0;
        }

        using var fileLogger = new FileLogger(options.LogPath ?? FileLogger.DefaultLogPath("runtime"));
        var logger = new CompositeLogger(
            fileLogger,
            new ConsoleLogger(options.Verbose ? LogLevel.Debug : LogLevel.Warning));

        logger.Info($"SteamDisc runtime starting. Disc root: '{options.DiscRoot}'.");

        try
        {
            return await InstallAsync(options, logger, fileLogger.Path).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.Error("Unhandled failure.", ex);
            Console.Error.WriteLine();
            Console.Error.WriteLine("Installation failed: " + ex.Message);
            Console.Error.WriteLine("A log was written to " + fileLogger.Path);
            return 1;
        }
    }

    private static async Task<int> InstallAsync(RuntimeOptions options, ISteamDiscLogger logger, string logPath)
    {
        var manifestPath = Path.Combine(options.DiscRoot, PayloadManifest.FileName);
        if (!File.Exists(manifestPath))
        {
            Console.Error.WriteLine($"No {PayloadManifest.FileName} found in '{options.DiscRoot}'.");
            Console.Error.WriteLine("Run this from the disc root, or pass the disc path as the first argument.");
            return 2;
        }

        var manifest = PayloadManifest.Load(manifestPath);

        var theme = Theme.LoadOrDefault(
            manifest.ThemePath is { Length: > 0 } themePath
                ? Path.Combine(options.DiscRoot, themePath)
                : null,
            out var themeError);

        if (themeError is not null)
        {
            logger.Warn($"Falling back to the default theme: {themeError}");
        }

        var tokens = new Dictionary<string, string>
        {
            ["title"] = manifest.Title,
            ["disc"] = manifest.Disc.Number.ToString(CultureInfo.InvariantCulture),
            ["discCount"] = manifest.Disc.Of.ToString(CultureInfo.InvariantCulture),
        };

        PrintBanner(manifest, theme, tokens);

        var steam = SteamLocator.Locate(options.SteamPath);
        if (steam is null)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Steam could not be found on this machine.");
            Console.Error.WriteLine("Install Steam and sign in, then run this installer again.");
            return 3;
        }

        var libraries = steam.GetLibraries();
        if (libraries.Count == 0)
        {
            Console.Error.WriteLine($"No Steam libraries found under '{steam.RootPath}'.");
            return 3;
        }

        var library = SelectLibrary(libraries, options, manifest);
        if (library is null)
        {
            Console.WriteLine("Cancelled.");
            return 4;
        }

        var host = new ConsoleInstallHost(theme, options.AssumeYes);
        var engine = new InstallEngine(host, logger);

        var request = new InstallRequest(manifest, options.DiscRoot, library, steam)
        {
            VerifyHashes = !options.SkipVerification,
            VerifyVolumesUpFront = options.VerifyVolumes,
            RunPrerequisites = !options.SkipPrerequisites,
            Launch = options.NoLaunch ? false : null,
            ValidateAfterInstall = options.Validate ? true : null,
            AutoUpdate = options.OnlyUpdateOnLaunch ? AutoUpdateBehavior.OnlyOnLaunch : AutoUpdateBehavior.Unchanged,
        };

        var preflight = engine.Preflight(request);

        Console.WriteLine();
        Console.WriteLine($"  Install to : {preflight.InstallPath}");
        Console.WriteLine($"  Needs      : {ConsoleProgressBar.FormatBytes(preflight.RequiredBytes)}");
        if (preflight.AvailableBytes is { } free)
        {
            Console.WriteLine($"  Free       : {ConsoleProgressBar.FormatBytes(free)}");
        }

        foreach (var warning in preflight.Warnings)
        {
            host.ReportWarning(warning);
        }

        if (!preflight.CanProceed)
        {
            Console.Error.WriteLine();
            foreach (var error in preflight.Errors)
            {
                Console.Error.WriteLine("  x " + error);
            }

            return 5;
        }

        if (!options.AssumeYes)
        {
            Console.WriteLine();
            var proceed = await host
                .ConfirmAsync(theme.String(ThemeStrings.InstallButton, tokens) + "?", CancellationToken.None)
                .ConfigureAwait(false);

            if (!proceed)
            {
                Console.WriteLine("Cancelled.");
                return 4;
            }
        }

        Console.WriteLine();

        var progress = new ConsoleProgressBar();
        using var cancellation = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        var result = await engine.InstallAsync(request, progress, cancellation.Token).ConfigureAwait(false);
        progress.Complete();

        Console.WriteLine();

        switch (result.Outcome)
        {
            case InstallOutcome.Succeeded:
                Console.WriteLine(theme.String(ThemeStrings.CompleteHeading, tokens));
                Console.WriteLine(theme.String(ThemeStrings.CompleteBody, tokens));
                Console.WriteLine(
                    $"  {ConsoleProgressBar.FormatBytes(result.BytesWritten)} in " +
                    $"{ConsoleProgressBar.FormatDuration(result.Duration)}");

                foreach (var warning in result.Warnings)
                {
                    host.ReportWarning(warning);
                }

                return 0;

            case InstallOutcome.Cancelled:
                Console.WriteLine("Cancelled. Nothing was registered with Steam.");
                return 4;

            default:
                Console.Error.WriteLine(theme.String(ThemeStrings.ErrorHeading, tokens));
                Console.Error.WriteLine("  " + (result.Error?.Message ?? "Unknown error."));
                Console.Error.WriteLine("  Log: " + logPath);
                return 1;
        }
    }

    private static void PrintBanner(
        PayloadManifest manifest,
        Theme theme,
        IReadOnlyDictionary<string, string> tokens)
    {
        Console.WriteLine();
        Console.WriteLine("  " + theme.String(ThemeStrings.WelcomeHeading, tokens));
        Console.WriteLine("  " + new string('=', Math.Max(4, manifest.Title.Length)));
        Console.WriteLine("  " + theme.String(ThemeStrings.WelcomeBody, tokens));

        if (!manifest.Disc.IsSingleDisc)
        {
            Console.WriteLine($"  Disc {manifest.Disc.Number} of {manifest.Disc.Of}.");
        }

        foreach (var advisory in manifest.Advisories)
        {
            var marker = advisory.Severity == AdvisorySeverity.Warning ? "  ! " : "  . ";
            Console.WriteLine(marker + advisory.Message);
        }
    }

    private static SteamLibrary? SelectLibrary(
        IReadOnlyList<SteamLibrary> libraries,
        RuntimeOptions options,
        PayloadManifest manifest)
    {
        if (options.LibraryPath is { Length: > 0 } explicitPath)
        {
            return new SteamLibrary(explicitPath);
        }

        if (libraries.Count == 1 || options.AssumeYes)
        {
            // Prefer a library with room over simply the first one.
            return libraries
                       .OrderByDescending(l => l.GetAvailableFreeBytes() ?? 0)
                       .FirstOrDefault(l => (l.GetAvailableFreeBytes() ?? long.MaxValue) > manifest.SizeOnDisk)
                   ?? libraries[0];
        }

        Console.WriteLine();
        Console.WriteLine("  Steam libraries:");
        for (var i = 0; i < libraries.Count; i++)
        {
            var free = libraries[i].GetAvailableFreeBytes();
            var freeText = free is { } bytes ? ConsoleProgressBar.FormatBytes(bytes) + " free" : "size unknown";
            Console.WriteLine($"    [{i + 1}] {libraries[i].Path}  ({freeText})");
        }

        Console.Write($"  Choose 1-{libraries.Count} (blank to cancel): ");
        var answer = Console.ReadLine()?.Trim();

        if (int.TryParse(answer, CultureInfo.InvariantCulture, out var choice) &&
            choice >= 1 && choice <= libraries.Count)
        {
            return libraries[choice - 1];
        }

        return null;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            SteamDisc runtime - installs a game from a SteamDisc disc or staging folder.

            Usage:
              Setup [disc-path] [options]

            Options:
              --library <path>       Steam library to install into
              --steam-path <path>    Steam installation root, if it cannot be found
              --yes                  Accept every prompt; pick a library automatically
              --verify-volumes       Check volume hashes before extracting
              --no-verify            Skip per-file hash checks while extracting (faster, riskier)
              --no-prerequisites     Do not run bundled redistributables
              --no-launch            Do not launch the game afterwards
              --validate             Ask Steam to verify the files after installing
              --update-on-launch     Set the app to update only when launched
              --log <path>           Write the log somewhere specific
              --verbose              Log debug detail to the console
              -h, --help             Show this help
            """);
    }
}

/// <summary>Command-line options for the runtime.</summary>
internal sealed class RuntimeOptions
{
    public string DiscRoot { get; private set; } = AppContext.BaseDirectory;

    public string? LibraryPath { get; private set; }

    public string? SteamPath { get; private set; }

    public string? LogPath { get; private set; }

    public bool AssumeYes { get; private set; }

    public bool VerifyVolumes { get; private set; }

    public bool SkipVerification { get; private set; }

    public bool SkipPrerequisites { get; private set; }

    public bool NoLaunch { get; private set; }

    public bool Validate { get; private set; }

    public bool OnlyUpdateOnLaunch { get; private set; }

    public bool Verbose { get; private set; }

    public bool ShowHelp { get; private set; }

    public static RuntimeOptions Parse(string[] args)
    {
        var options = new RuntimeOptions();
        var expectingDiscRoot = true;

        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];

            if (!argument.StartsWith('-'))
            {
                if (expectingDiscRoot)
                {
                    options.DiscRoot = Path.GetFullPath(argument);
                    expectingDiscRoot = false;
                }

                continue;
            }

            switch (argument.ToLowerInvariant())
            {
                case "--library":
                    options.LibraryPath = Next(args, ref i);
                    break;
                case "--steam-path":
                    options.SteamPath = Next(args, ref i);
                    break;
                case "--log":
                    options.LogPath = Next(args, ref i);
                    break;
                case "--yes" or "-y":
                    options.AssumeYes = true;
                    break;
                case "--verify-volumes":
                    options.VerifyVolumes = true;
                    break;
                case "--no-verify":
                    options.SkipVerification = true;
                    break;
                case "--no-prerequisites":
                    options.SkipPrerequisites = true;
                    break;
                case "--no-launch":
                    options.NoLaunch = true;
                    break;
                case "--validate":
                    options.Validate = true;
                    break;
                case "--update-on-launch":
                    options.OnlyUpdateOnLaunch = true;
                    break;
                case "--verbose" or "-v":
                    options.Verbose = true;
                    break;
                case "--help" or "-h" or "-?":
                    options.ShowHelp = true;
                    break;
            }
        }

        return options;
    }

    private static string? Next(string[] args, ref int index)
        => index + 1 < args.Length ? args[++index] : null;
}
