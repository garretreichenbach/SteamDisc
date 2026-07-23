using System.Globalization;
using SteamDisc.Authoring;
using SteamDisc.Core.Payload;
using SteamDisc.Core.Steam;

namespace SteamDisc.Builder.Commands;

/// <summary>Commands that inspect the local Steam libraries.</summary>
internal static class GameCommands
{
    public static int List(CommandLine command)
    {
        var steam = SteamLocator.LocateRequired(command.Value("steam-path"));
        var catalog = new GameCatalog(steam);
        var games = catalog.List(measureSizes: command.Has("measure"));

        Console.WriteLine($"Steam: {steam.RootPath}");
        foreach (var library in steam.GetLibraries())
        {
            Console.WriteLine($"  library: {library.Path}");
        }

        Console.WriteLine();

        if (games.Count == 0)
        {
            Console.WriteLine("No installed games found.");
            return 0;
        }

        Console.WriteLine($"{"AppID",-8} {"Size",12} {"Updated",-12} {"Fit",-8} Title");
        Console.WriteLine(new string('-', 78));

        foreach (var game in games.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var size = game.MeasuredSize > 0 ? Format.Bytes(game.MeasuredSize) : "?";
            var updated = game.LastUpdated.ToUnixTimeSeconds() > 0
                ? game.LastUpdated.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : "unknown";

            Console.WriteLine(
                $"{game.AppId,-8} {size,12} {updated,-12} {game.Suitability,-8} {game.Name}");
        }

        Console.WriteLine();
        Console.WriteLine("'Fit' is a hint: Ideal = not patched in a year, Caveats = see 'inspect'.");
        return 0;
    }

    public static int Inspect(CommandLine command)
    {
        var steam = SteamLocator.LocateRequired(command.Value("steam-path"));
        var game = Resolve(steam, command.PositionalAt(1));

        if (game is null)
        {
            return 1;
        }

        var manifest = game.App.Manifest;

        Console.WriteLine($"{manifest.Name}  ({game.AppId})");
        Console.WriteLine(new string('=', manifest.Name.Length + 12));
        Console.WriteLine($"  Library      : {game.App.Library.Path}");
        Console.WriteLine($"  Install dir  : {game.InstallPath}");
        Console.WriteLine($"  Measured size: {Format.Bytes(game.MeasuredSize)}");
        Console.WriteLine($"  Manifest size: {Format.Bytes(manifest.SizeOnDisk)}");
        Console.WriteLine($"  Build id     : {manifest.BuildId}");
        Console.WriteLine($"  Last updated : {manifest.LastUpdated:yyyy-MM-dd HH:mm} UTC");
        Console.WriteLine($"  State flags  : {manifest.StateFlags} ({(int)manifest.StateFlags})");
        Console.WriteLine($"  Owner        : {manifest.LastOwner}");

        Console.WriteLine($"  Depots       : {manifest.InstalledDepots.Count}");
        foreach (var depot in manifest.InstalledDepots)
        {
            Console.WriteLine(
                $"    {depot.DepotId,-10} manifest {depot.ManifestId,-22} {Format.Bytes(depot.Size),12}");
        }

        var prerequisites = PrerequisiteScanner.Scan(game.InstallPath);
        Console.WriteLine($"  Redistributables: {prerequisites.Count}");
        foreach (var prerequisite in prerequisites)
        {
            Console.WriteLine($"    {prerequisite.Name}  ({prerequisite.Path})");
        }

        // The manifest audit is the single most useful thing here: it predicts the failure the
        // whole Path B approach exists to avoid, before a disc is burned rather than after.
        var problems = AppManifestTransplant.Audit(manifest);
        Console.WriteLine();
        if (problems.Count == 0)
        {
            Console.WriteLine("  Manifest audit: clean. Steam should accept a transplant of this manifest.");
        }
        else
        {
            Console.WriteLine("  Manifest audit:");
            foreach (var problem in problems)
            {
                Console.WriteLine("    x " + problem);
            }
        }

        if (game.Advisories.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  Advisories:");
            foreach (var advisory in game.Advisories)
            {
                var marker = advisory.Severity == AdvisorySeverity.Warning ? "    ! " : "    . ";
                Console.WriteLine(marker + advisory.Message);
            }
        }

        return 0;
    }

    /// <summary>Resolves a game by app id or by name, reporting ambiguity clearly.</summary>
    public static GameCandidate? Resolve(SteamInstallation steam, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            Console.Error.WriteLine("Specify a game by AppID or name.");
            return null;
        }

        var catalog = new GameCatalog(steam);

        if (uint.TryParse(query, CultureInfo.InvariantCulture, out var appId))
        {
            var byId = catalog.Find(appId);
            if (byId is null)
            {
                Console.Error.WriteLine($"App {appId} is not installed in any library on this machine.");
            }

            return byId;
        }

        var matches = catalog.Search(query);

        switch (matches.Count)
        {
            case 0:
                Console.Error.WriteLine($"No installed game matches '{query}'.");
                return null;

            case 1:
                return catalog.Find(matches[0].AppId) ?? matches[0];

            default:
                Console.Error.WriteLine($"'{query}' matches {matches.Count} games:");
                foreach (var match in matches)
                {
                    Console.Error.WriteLine($"  {match.AppId,-8} {match.Name}");
                }

                Console.Error.WriteLine("Use the AppID to choose one.");
                return null;
        }
    }
}

/// <summary>Shared formatting for CLI output.</summary>
internal static class Format
{
    public static string Bytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString(value >= 100 ? "F0" : "F1", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    public static string Duration(TimeSpan duration) => duration.TotalHours >= 1
        ? duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
        : duration.ToString(@"m\:ss", CultureInfo.InvariantCulture);
}
