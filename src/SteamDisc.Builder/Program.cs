using SteamDisc.Builder;
using SteamDisc.Builder.Commands;
using SteamDisc.Core.Diagnostics;
using SteamDisc.Core.Steam;
using SteamDisc.Core.Theming;
using SteamDisc.Imaging;
using SteamDisc.Authoring;

// The Builder: the authoring half of SteamDisc.
//
// A CLI rather than a GUI at this stage, on purpose. Everything below drives the same
// engines a graphical Builder would (GameCatalog, PackageBuilder, Iso9660Builder,
// CoverRenderer), so the GUI becomes a view over working code rather than a place where
// authoring logic accumulates. It also answers the plan's open question 3 in the affirmative:
// a shelf's worth of discs can be authored from a script.

return await BuilderProgram.RunAsync(args).ConfigureAwait(false);

internal static class BuilderProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        var command = CommandLine.Parse(args);
        var verb = args[0].ToLowerInvariant();

        var logger = new ConsoleLogger(command.Has("verbose") ? LogLevel.Debug : LogLevel.Warning);

        try
        {
            return verb switch
            {
                "list" => GameCommands.List(command),
                "inspect" => GameCommands.Inspect(command),
                "package" => await PackageCommands.PackageAsync(command, logger).ConfigureAwait(false),
                "iso" => await PackageCommands.BuildIsoAsync(command).ConfigureAwait(false),
                "burn" => await PackageCommands.BurnAsync(command).ConfigureAwait(false),
                "verify" => await PackageCommands.VerifyAsync(command).ConfigureAwait(false),
                "themes" => ListThemes(),
                "media" => ListMedia(),
                "covers" => await CoversAsync(command, logger).ConfigureAwait(false),
                _ => Unknown(verb),
            };
        }
        catch (SteamNotFoundException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 3;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException
                                       or NotSupportedException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Error: " + ex.Message);
            return 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 4;
        }
    }

    private static async Task<int> CoversAsync(CommandLine command, ISteamDiscLogger logger)
    {
        var subcommand = (command.PositionalAt(1) ?? "templates").ToLowerInvariant();

        return subcommand switch
        {
            "templates" or "list" => CoverCommands.ListTemplates(command),
            "import" => CoverCommands.ImportTemplate(command),
            "new" => await CoverCommands.NewAsync(command, logger).ConfigureAwait(false),
            "render" => CoverCommands.Render(command),
            "print" => CoverCommands.PrintFinished(command),
            _ => UnknownCoverSubcommand(subcommand),
        };
    }

    private static int UnknownCoverSubcommand(string subcommand)
    {
        Console.Error.WriteLine($"Unknown covers subcommand '{subcommand}'.");
        Console.Error.WriteLine("Try: templates, import, new, render, print.");
        return 1;
    }

    private static int ListThemes()
    {
        Console.WriteLine("Built-in themes:");
        foreach (var (id, factory) in BuiltInThemes.All)
        {
            var theme = factory();
            Console.WriteLine($"  {id,-14} {theme.Name,-24} layout: {theme.Layout}");
        }

        Console.WriteLine();
        Console.WriteLine("Pass --theme <id>, or --theme <folder> to use a theme folder of your own.");
        return 0;
    }

    private static int ListMedia()
    {
        Console.WriteLine("Target media:");
        foreach (var medium in OpticalMedium.All)
        {
            Console.WriteLine($"  {medium.Id,-14} {medium}");
        }

        Console.WriteLine();
        Console.WriteLine(
            $"About {Format.Bytes(DiscSpanPlanner.PerDiscOverheadBytes)} per disc is reserved for the " +
            "runtime, theme and image structures.");
        return 0;
    }

    private static int Unknown(string verb)
    {
        Console.Error.WriteLine($"Unknown command '{verb}'.");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            SteamDisc Builder - turn an owned Steam game into physical media.

            Commands:
              list [--media <id>]           List installed games; with --media, how many discs each needs
              inspect <appid|name>          Show a game's manifest, depots and advisories
              package <appid|name>          Package a game into disc staging folders
              iso <staging-folder>          Build a burnable ISO from a staging folder
              burn <iso>                    Hand an ISO to a disc burner
              verify <disc-folder>          Check a built disc without installing it
              themes                        List built-in installer themes
              media                         List target media and capacities
              covers <subcommand>           Cover Studio - see below

            Cover Studio:
              covers templates              List cover templates
              covers import <path>          Import downloaded template artwork
              covers new                    Create a cover project
              covers render [project]       Render a print-ready PDF
              covers print <image>          Print a cover somebody else already finished

            Common options:
              --steam-path <path>           Steam root, if it cannot be found automatically
              --out <path>                  Output path
              --verbose                     Log debug detail

            package options:
              --media <id>                  Target media (default bd-r); see 'media'
              --compression <level>         store | fast | balanced | maximum (default fast)
              --format <id>                 sdz (default) or 7z
              --volume-size <size>          Override the volume size, e.g. 2g
              --theme <id|folder>           Installer theme (default classic)
              --runtime <path>              Published Setup.exe to place on the disc
              --art-dir <path>              Folder of your own artwork to prefer
              --steamgriddb-key <key>       SteamGridDB API key
              --no-art                      Do not fetch artwork
              --no-hashes                   Skip the SHA-256 sidecar
              --validate                    Ask Steam to verify files after installing

            covers options:
              --template <id>               Template to use (default sgc-dvd)
              --template-dir <path>         Template library location
              --disc <folder>               Take the title and app id from a staging folder
              --game <appid|name>           Take the title and app id from an installed game
              --title <text>                Set the title directly
              --media <type>                discLabel | jewelCase | dvdCase | bluRayCase
              --attribution <text>          Credit recorded on an imported template
              --proof                       Draw slot outlines when rendering

            Examples:
              steamdisc list --media dvd
              steamdisc package 620 --media bd-r --out ~/discs/portal2
              steamdisc iso ~/discs/portal2/disc --out ~/discs/portal2.iso
              steamdisc covers new --game 620 --template blank-bluray --out ~/discs/portal2/cover.json
              steamdisc covers render ~/discs/portal2/cover.json
            """);
    }
}
