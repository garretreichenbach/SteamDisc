using SteamDisc.Art;
using SteamDisc.Authoring;
using SteamDisc.Core.Archive;
using SteamDisc.Core.Diagnostics;
using SteamDisc.Core.Payload;
using SteamDisc.Core.Steam;
using SteamDisc.Core.Theming;
using SteamDisc.Imaging;
using SteamDisc.Imaging.Iso;

namespace SteamDisc.Builder.Commands;

/// <summary>Commands that turn an installed game into disc staging folders and images.</summary>
internal static class PackageCommands
{
    public static async Task<int> PackageAsync(CommandLine command, ISteamDiscLogger logger)
    {
        var steam = SteamLocator.LocateRequired(command.Value("steam-path"));
        var game = GameCommands.Resolve(steam, command.PositionalAt(1));
        if (game is null)
        {
            return 1;
        }

        var medium = OpticalMedium.Find(command.Value("media", "bd-r"));
        if (medium is null)
        {
            Console.Error.WriteLine(
                $"Unknown media '{command.Value("media")}'. Options: " +
                string.Join(", ", OpticalMedium.All.Select(m => m.Id)) + ".");
            return 1;
        }

        var output = command.Value("out")
                     ?? Path.Combine(Environment.CurrentDirectory, Sanitise(game.Name));

        var compression = ParseCompression(command.Value("compression", "fast"));
        if (compression is null)
        {
            Console.Error.WriteLine("Unknown --compression. Options: store, fast, balanced, maximum.");
            return 1;
        }

        var theme = ResolveTheme(command.Value("theme", "classic"));
        if (theme is null)
        {
            Console.Error.WriteLine(
                "Unknown --theme. Built-ins: " + string.Join(", ", BuiltInThemes.All.Keys) +
                ", or pass a path to a theme folder.");
            return 1;
        }

        IReadOnlyDictionary<string, string>? artwork = null;
        ArtSidecar? sidecar = null;

        if (!command.Has("no-art"))
        {
            (artwork, sidecar) = await FetchArtworkAsync(game, command, logger).ConfigureAwait(false);
        }

        Console.WriteLine($"Packaging {game.Name} ({game.AppId})");
        Console.WriteLine($"  Source     : {game.InstallPath}");
        Console.WriteLine($"  Size       : {Format.Bytes(game.MeasuredSize)}");
        Console.WriteLine($"  Media      : {medium.Name}");
        Console.WriteLine($"  Compression: {compression}");
        Console.WriteLine($"  Output     : {output}");

        foreach (var advisory in game.Advisories)
        {
            Console.WriteLine((advisory.Severity == AdvisorySeverity.Warning ? "  ! " : "  . ") + advisory.Message);
        }

        Console.WriteLine();

        var request = new PackageRequest(game, output, medium)
        {
            Compression = compression.Value,
            ArchiveFormat = command.Value("format", ArchiveFormats.Sdz),
            VolumeSize = command.Size("volume-size") ?? 0,
            Theme = theme,
            Artwork = artwork,
            RuntimeExecutablePath = command.Value("runtime"),
            WriteHashSidecar = !command.Has("no-hashes"),
            Validate = command.Has("validate"),
            LaunchAfterInstall = !command.Has("no-launch"),
        };

        var progress = new ConsoleProgressReporter();
        var builder = new PackageBuilder(logger: logger);

        PackageResult result;
        try
        {
            result = await builder.BuildAsync(request, progress).ConfigureAwait(false);
        }
        finally
        {
            progress.Complete();
        }

        Console.WriteLine();
        Console.WriteLine($"Packaged in {Format.Duration(result.Duration)}.");
        Console.WriteLine(
            $"  {Format.Bytes(result.UncompressedBytes)} -> {Format.Bytes(result.CompressedBytes)} " +
            $"({result.CompressionRatio:P0} of original)");
        Console.WriteLine($"  Discs: {result.Plan.DiscCount} × {medium.Name}");

        for (var disc = 1; disc <= result.Plan.DiscCount; disc++)
        {
            Console.WriteLine($"    disc {disc}: {Format.Bytes(result.Plan.BytesOnDisc(disc))}  {result.DiscRoots[disc - 1]}");
        }

        sidecar?.Save(Path.Combine(result.DiscRoots[0], ArtSidecar.FileName));

        if (request.RuntimeExecutablePath is null)
        {
            Console.WriteLine();
            Console.WriteLine("Note: no --runtime was given, so the discs have no Setup.exe.");
            Console.WriteLine("      Publish SteamDisc.Runtime and pass its path to make them self-contained.");
        }

        Console.WriteLine();
        Console.WriteLine("Next:");
        Console.WriteLine($"  Test it   : dotnet run --project src/SteamDisc.Runtime -- \"{result.DiscRoots[0]}\"");
        Console.WriteLine($"  Make ISOs : steamdisc iso \"{result.DiscRoots[0]}\"");

        return 0;
    }

    public static async Task<int> BuildIsoAsync(CommandLine command)
    {
        var source = command.PositionalAt(1);
        if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source))
        {
            Console.Error.WriteLine("Specify the disc staging folder to turn into an ISO.");
            return 1;
        }

        var manifestPath = Path.Combine(source, PayloadManifest.FileName);
        var label = File.Exists(manifestPath)
            ? PayloadManifest.Load(manifestPath).Disc.Label ?? PayloadManifest.Load(manifestPath).Title
            : Path.GetFileName(Path.TrimEndingDirectorySeparator(source));

        var output = command.Value("out") ?? Path.ChangeExtension(Path.TrimEndingDirectorySeparator(source), ".iso");

        var builder = new Iso9660Builder();
        var estimate = builder.EstimateSize(source);

        Console.WriteLine($"Building {output}");
        Console.WriteLine($"  Source   : {source}");
        Console.WriteLine($"  Estimated: {Format.Bytes(estimate)}");

        if (command.Value("media") is { } mediaId && OpticalMedium.Find(mediaId) is { } medium)
        {
            if (estimate > medium.CapacityBytes)
            {
                Console.Error.WriteLine(
                    $"  x That is {Format.Bytes(estimate - medium.CapacityBytes)} more than {medium.Name} holds.");
                return 1;
            }

            Console.WriteLine(
                $"  Fits {medium.Name} with {Format.Bytes(medium.CapacityBytes - estimate)} to spare.");
        }

        Console.WriteLine();

        var progress = new ConsoleProgressReporter();
        IsoBuildResult result;
        try
        {
            result = await builder
                .BuildAsync(source, output, new IsoBuildOptions(label), progress)
                .ConfigureAwait(false);
        }
        finally
        {
            progress.Complete();
        }

        Console.WriteLine();
        Console.WriteLine($"Wrote {result.Path}");
        Console.WriteLine($"  {Format.Bytes(result.SizeBytes)}, {result.FileCount} files, {result.DirectoryCount} directories.");
        return 0;
    }

    public static async Task<int> BurnAsync(CommandLine command)
    {
        var iso = command.PositionalAt(1);
        if (string.IsNullOrWhiteSpace(iso) || !File.Exists(iso))
        {
            Console.Error.WriteLine("Specify the ISO to burn.");
            return 1;
        }

        var burners = DiscBurners.Discover(command.Value("burner"));
        if (burners.Count == 0)
        {
            Console.Error.WriteLine("No disc burner was found on this machine.");
            Console.Error.WriteLine(
                "On Windows, the built-in Disc Image Burner handles this; elsewhere, burn the ISO with your own tool.");
            return 1;
        }

        var burner = burners[0];
        Console.WriteLine($"Handing {Path.GetFileName(iso)} to {burner.Name}.");

        var result = await burner.BurnAsync(iso).ConfigureAwait(false);
        Console.WriteLine("  " + result.Message);
        return result.Started ? 0 : 1;
    }

    /// <summary>Checks a built staging folder or mounted disc without installing anything.</summary>
    public static async Task<int> VerifyAsync(CommandLine command)
    {
        var root = command.PositionalAt(1) ?? Environment.CurrentDirectory;
        var manifestPath = Path.Combine(root, PayloadManifest.FileName);

        if (!File.Exists(manifestPath))
        {
            Console.Error.WriteLine($"No {PayloadManifest.FileName} in '{root}'.");
            return 1;
        }

        var manifest = PayloadManifest.Load(manifestPath);
        Console.WriteLine($"{manifest.Title} ({manifest.AppId}) — disc {manifest.Disc.Number} of {manifest.Disc.Of}");
        Console.WriteLine($"  Set id : {manifest.Disc.SetId}");
        Console.WriteLine($"  Format : {manifest.Archive.Format}, {manifest.Archive.Volumes.Count} volume(s)");

        var problems = manifest.Validate().ToList();

        foreach (var volume in manifest.Archive.Volumes.Where(v => v.Disc == manifest.Disc.Number))
        {
            var path = Path.Combine(root, volume.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                problems.Add($"Volume {volume.Index} is missing ({volume.Path}).");
                continue;
            }

            var actualSize = new FileInfo(path).Length;
            if (volume.Size > 0 && actualSize != volume.Size)
            {
                problems.Add($"Volume {volume.Index} is {actualSize} bytes; the manifest says {volume.Size}.");
                continue;
            }

            if (volume.Sha256 is { Length: > 0 } expected)
            {
                Console.Write($"  Hashing {volume.Path} ... ");
                var actual = await Core.Hashing.Sha256File.ComputeAsync(path).ConfigureAwait(false);
                var ok = string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
                Console.WriteLine(ok ? "ok" : "MISMATCH");
                if (!ok)
                {
                    problems.Add($"Volume {volume.Index} failed its hash check.");
                }
            }
        }

        var appManifestPath = Path.Combine(root, manifest.AppManifestPath);
        if (string.IsNullOrWhiteSpace(manifest.AppManifestPath) || !File.Exists(appManifestPath))
        {
            problems.Add("No captured app manifest on this disc; Steam will very likely re-download the game.");
        }
        else
        {
            var appManifest = AppManifest.Load(appManifestPath);
            problems.AddRange(AppManifestTransplant.Audit(appManifest).Select(p => "App manifest: " + p));
        }

        Console.WriteLine();
        if (problems.Count == 0)
        {
            Console.WriteLine("Disc looks good.");
            return 0;
        }

        foreach (var problem in problems)
        {
            Console.Error.WriteLine("  x " + problem);
        }

        return 1;
    }

    private static async Task<(IReadOnlyDictionary<string, string>? Artwork, ArtSidecar? Sidecar)> FetchArtworkAsync(
        GameCandidate game,
        CommandLine command,
        ISteamDiscLogger logger)
    {
        var localDirectories = command.Value("art-dir") is { Length: > 0 } dir ? new[] { dir } : null;
        var resolver = ArtResolver.CreateDefault(command.Value("steamgriddb-key"), localDirectories, logger: logger);

        Console.WriteLine("Fetching artwork...");

        IReadOnlyDictionary<string, ArtAsset> assets;
        try
        {
            assets = await resolver.ResolveAsync(game.AppId, game.Name).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Console.WriteLine("  Could not reach the art providers; continuing without artwork.");
            return (null, null);
        }

        if (assets.Count == 0)
        {
            Console.WriteLine("  No artwork found; the disc will use the theme's colours alone.");
            return (null, null);
        }

        var sidecar = new ArtSidecar { AppId = game.AppId, Title = game.Name };
        var artwork = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (slot, asset) in assets)
        {
            artwork[slot] = asset.LocalPath;
            sidecar.Record(slot, asset);
            Console.WriteLine($"  {slot,-10} {asset.Candidate.ProviderId} {asset.Candidate.Url}");
        }

        return (artwork, sidecar);
    }

    private static ThemeDefinition? ResolveTheme(string idOrPath)
    {
        if (BuiltInThemes.Get(idOrPath) is { } builtIn)
        {
            return builtIn;
        }

        if (Directory.Exists(idOrPath))
        {
            var file = Path.Combine(idOrPath, ThemeDefinition.FileName);
            if (File.Exists(file))
            {
                return ThemeDefinition.Parse(File.ReadAllText(file));
            }
        }

        return null;
    }

    private static ArchiveCompression? ParseCompression(string value)
        => Enum.TryParse<ArchiveCompression>(value, ignoreCase: true, out var result) ? result : null;

    private static string Sanitise(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
    }
}
