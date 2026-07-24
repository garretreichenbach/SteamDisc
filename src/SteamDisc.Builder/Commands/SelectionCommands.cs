using SteamDisc.Authoring;
using SteamDisc.Core.Steam;

namespace SteamDisc.Builder.Commands;

/// <summary>Commands for choosing which parts of a game go onto the disc.</summary>
internal static class SelectionCommands
{
    public static int Select(CommandLine command)
    {
        var steam = SteamLocator.LocateRequired(command.Value("steam-path"));
        var game = GameCommands.Resolve(steam, command.PositionalAt(1));
        if (game is null)
        {
            return 1;
        }

        var selection = SelectionManifest.Build(game, applyHeuristics: !command.Has("include-all"));

        var output = command.Value("out")
                     ?? Path.Combine(Environment.CurrentDirectory, Sanitise(game.Name) + SelectionManifest.Extension);

        selection.Save(output);

        Console.WriteLine($"Selection for {game.Name} ({game.AppId})");
        Console.WriteLine($"  Source : {game.InstallPath}");
        Console.WriteLine($"  Written: {output}");
        Console.WriteLine();
        Console.WriteLine($"  {"Keep",-4} {"Size",12}  Path");
        Console.WriteLine("  " + new string('-', 60));

        foreach (var entry in selection.Entries)
        {
            var mark = entry.Include ? " on " : " off";
            var suffix = entry.Kind == SelectionEntryKind.Folder ? "/" : string.Empty;
            Console.WriteLine($"  {mark,-4} {Format.Bytes(entry.Size),12}  {entry.Path}{suffix}");
            if (entry.Reason is { Length: > 0 } reason)
            {
                Console.WriteLine($"                      -> {reason}");
            }
        }

        var excluded = selection.Excluded;
        Console.WriteLine();
        if (excluded.Count == 0)
        {
            Console.WriteLine("Nothing excluded by default. Edit the file to turn any entry 'off', then:");
        }
        else
        {
            Console.WriteLine(
                $"Excluded by default: {excluded.Count} item(s), {Format.Bytes(selection.ExcludedBytes)} saved. " +
                "These are heuristic guesses — review the file and adjust 'include' as you like, then:");
        }

        Console.WriteLine($"  steamdisc package {game.AppId} --selection \"{output}\"");
        Console.WriteLine();
        Console.WriteLine(
            "Note: excluding files Steam still lists as installed means a Steam 'verify integrity'");
        Console.WriteLine(
            "      may re-download them. Dropping whole optional or language content is the safe case.");
        return 0;
    }

    /// <summary>
    /// Loads and validates a <c>--selection</c> file for a package run, printing what it will drop
    /// and any staleness warnings. Returns null (and prints why) when the file cannot be used.
    /// </summary>
    public static SelectionManifest? LoadForPackage(string path, GameCandidate game)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"Selection file '{path}' does not exist.");
            return null;
        }

        SelectionManifest selection;
        try
        {
            selection = SelectionManifest.Load(path);
        }
        catch (InvalidDataException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return null;
        }

        if (selection.AppId != game.AppId)
        {
            // A selection for the wrong game would silently drop the wrong paths; refuse it.
            Console.Error.WriteLine(
                $"Selection file is for app {selection.AppId} ({selection.Title}), " +
                $"not {game.AppId} ({game.Name}).");
            return null;
        }

        foreach (var warning in selection.CheckAgainst(game))
        {
            Console.WriteLine("  ! " + warning);
        }

        var excluded = selection.Excluded;
        if (excluded.Count > 0)
        {
            Console.WriteLine(
                $"  Selection: excluding {excluded.Count} item(s), {Format.Bytes(selection.ExcludedBytes)}:");
            foreach (var entry in excluded)
            {
                Console.WriteLine($"    - {entry.Path}");
            }

            Console.WriteLine(
                "  ! Steam still believes these are installed; a 'verify integrity' may re-download them.");
        }
        else
        {
            Console.WriteLine("  Selection: nothing excluded (packing the full folder).");
        }

        return selection;
    }

    private static string Sanitise(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
    }
}
