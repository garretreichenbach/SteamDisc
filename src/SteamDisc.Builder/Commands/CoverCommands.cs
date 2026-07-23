using SteamDisc.Art;
using SteamDisc.Core.Diagnostics;
using SteamDisc.Core.Payload;
using SteamDisc.Core.Steam;
using SteamDisc.Covers;

namespace SteamDisc.Builder.Commands;

/// <summary>
/// The Cover Studio: templates, artwork, and print-ready PDFs for cases and disc labels.
/// </summary>
/// <remarks>
/// This is the physical-media half of the project — a burned disc in a plain sleeve is not the
/// retail-box experience the whole thing is aiming at. The commands here mirror what the GUI's
/// Cover Studio tab drives, and share every line of the rendering path with it.
/// </remarks>
internal static class CoverCommands
{
    public static int ListTemplates(CommandLine command)
    {
        var templates = CoverTemplateCatalog.Discover(command.Value("template-dir"));

        Console.WriteLine($"{"ID",-22} {"Media",-12} {"Trim (mm)",-16} {"Art",-5} Name");
        Console.WriteLine(new string('-', 92));

        foreach (var group in templates.GroupBy(t => t.Family).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"[{group.Key}]");
            foreach (var template in group.OrderBy(t => t.Id, StringComparer.Ordinal))
            {
                var trim = $"{template.Trim.Width:0.#}×{template.Trim.Height:0.#}";
                var hasArt = template.ResolveAsset(template.OverlayPath) is not null ? "yes" : "-";
                Console.WriteLine(
                    $"  {template.Id,-20} {template.Media,-12} {trim,-16} {hasArt,-5} {template.Name}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"User templates live in {command.Value("template-dir") ?? CoverTemplateCatalog.DefaultUserTemplateDirectory}");
        Console.WriteLine("Blank layouts print without a design. Steam layouts need their artwork imported:");
        Console.WriteLine("  steamdisc covers import \"<downloaded template folder>\"");
        Console.WriteLine("To print a cover somebody else already finished:");
        Console.WriteLine("  steamdisc covers print <image>");
        return 0;
    }

    public static int ImportTemplate(CommandLine command)
    {
        var source = command.PositionalAt(2);
        if (string.IsNullOrWhiteSpace(source))
        {
            Console.Error.WriteLine("Specify a template file, folder or zip to import.");
            Console.Error.WriteLine();
            Console.Error.WriteLine(
                "Templates can be downloaded from steamgamecovers.com/template.php — disc labels,");
            Console.Error.WriteLine(
                "jewel, DVD and Blu-ray cases in several styles, as JPG/PNG/PSD at 300 DPI.");
            Console.Error.WriteLine(
                "Download the ones you want, then point this command at them. Their designs stay");
            Console.Error.WriteLine(
                "intact and are composited over your own key art.");
            return 1;
        }

        var attribution = command.Value("attribution");
        var family = command.Value("family");
        var destination = command.Value("template-dir");

        CoverMedia? media = null;
        if (command.Value("media") is { Length: > 0 } mediaValue)
        {
            if (!Enum.TryParse<CoverMedia>(mediaValue, ignoreCase: true, out var parsed))
            {
                Console.Error.WriteLine(
                    "Unknown --media. Options: " + string.Join(", ", Enum.GetNames<CoverMedia>()) + ".");
                return 1;
            }

            media = parsed;
        }

        try
        {
            var results = File.Exists(source) && !source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                ? new[] { TemplatePackImporter.ImportArtwork(source, destination, media, attribution, family) }
                : TemplatePackImporter.ImportPack(source, destination, attribution, family).ToArray();

            foreach (var result in results)
            {
                Console.WriteLine($"Imported {result.Template.Id}  ({result.Template.Media})");
                Console.WriteLine($"  {result.Directory}");
                foreach (var note in result.Notes)
                {
                    Console.WriteLine("  . " + note);
                }

                Console.WriteLine();
            }

            Console.WriteLine($"{results.Length} template(s) imported.");
            return 0;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or IOException)
        {
            Console.Error.WriteLine("Import failed: " + ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// Prints a cover somebody else already finished — the common case for the community
    /// gallery, where the artwork is already composited into the template design.
    /// </summary>
    public static int PrintFinished(CommandLine command)
    {
        var image = command.PositionalAt(2);
        if (string.IsNullOrWhiteSpace(image))
        {
            Console.Error.WriteLine("Specify the finished cover image to print.");
            Console.Error.WriteLine();
            Console.Error.WriteLine(
                "Use this for covers downloaded from a gallery, where the art is already part of");
            Console.Error.WriteLine(
                "the design. It works out which case the sheet is for and prints it at true size.");
            return 1;
        }

        CoverMedia? media = null;
        if (command.Value("media") is { Length: > 0 } mediaValue)
        {
            if (!Enum.TryParse<CoverMedia>(mediaValue, ignoreCase: true, out var parsed))
            {
                Console.Error.WriteLine(
                    "Unknown --media. Options: " + string.Join(", ", Enum.GetNames<CoverMedia>()) + ".");
                return 1;
            }

            media = parsed;
        }

        var prepared = FinishedCoverImporter.Prepare(image, media, command.Value("title"));

        foreach (var note in prepared.Notes)
        {
            Console.WriteLine("  . " + note);
        }

        var output = command.Value("out") ?? Path.ChangeExtension(image, ".pdf");
        var result = new CoverRenderer().Render(prepared.Project, prepared.Template, output);

        Console.WriteLine();
        Console.WriteLine($"Wrote {result.OutputPath}");
        Console.WriteLine($"  Page: {result.PageSize}");

        foreach (var warning in result.Warnings)
        {
            Console.WriteLine("  ! " + warning);
        }

        PrintTerms(prepared.Template);
        PrintScaleReminder();
        return 0;
    }

    /// <summary>Creates a cover project, optionally pulling artwork for its slots automatically.</summary>
    public static async Task<int> NewAsync(CommandLine command, ISteamDiscLogger logger)
    {
        var templateId = command.Value("template", CoverTemplateCatalog.DefaultTemplateId);
        var template = CoverTemplateCatalog.Find(templateId, command.Value("template-dir"));

        if (template is null)
        {
            Console.Error.WriteLine($"No template '{templateId}'. Run 'covers templates' to see what is available.");
            return 1;
        }

        var project = new CoverProject { TemplateId = template.Id };

        // A disc staging folder supplies the title and app id for free.
        var discRoot = command.Value("disc");
        if (discRoot is { Length: > 0 } && File.Exists(Path.Combine(discRoot, PayloadManifest.FileName)))
        {
            var manifest = PayloadManifest.Load(Path.Combine(discRoot, PayloadManifest.FileName));
            project.Title = manifest.Title;
            project.AppId = manifest.AppId;
        }
        else if (command.Value("game") is { Length: > 0 } gameQuery)
        {
            var steam = SteamLocator.LocateRequired(command.Value("steam-path"));
            var game = GameCommands.Resolve(steam, gameQuery);
            if (game is null)
            {
                return 1;
            }

            project.Title = game.Name;
            project.AppId = game.AppId;
        }

        if (command.Value("title") is { Length: > 0 } explicitTitle)
        {
            project.Title = explicitTitle;
        }

        if (!command.Has("no-art") && project.AppId != 0)
        {
            await FillSlotsWithArtAsync(project, template, command, logger).ConfigureAwait(false);
        }

        var output = command.Value("out")
                     ?? Path.Combine(Environment.CurrentDirectory, CoverProject.FileName);

        project.Save(output);

        Console.WriteLine($"Wrote {output}");
        Console.WriteLine($"  Template: {template.Name} ({template.Id})");
        Console.WriteLine($"  Page    : {template.Page}, trim {template.Trim.Width:0.#}×{template.Trim.Height:0.#} mm");
        Console.WriteLine("  Slots   :");

        foreach (var slot in template.Slots)
        {
            var art = project.ResolveArtwork(slot.Id);
            Console.WriteLine(
                $"    {slot.Id,-12} {slot.Bounds}  {(art is null ? "(empty)" : Path.GetFileName(art))}");
        }

        Console.WriteLine();
        Console.WriteLine("Edit the artwork paths in that file, then: steamdisc covers render " + output);
        return 0;
    }

    public static int Render(CommandLine command)
    {
        var projectPath = command.PositionalAt(2) ?? CoverProject.FileName;
        if (!File.Exists(projectPath))
        {
            Console.Error.WriteLine($"No cover project at '{projectPath}'. Create one with 'covers new'.");
            return 1;
        }

        var project = CoverProject.Load(projectPath);
        var template = CoverTemplateCatalog.Find(project.TemplateId, command.Value("template-dir"));

        if (template is null)
        {
            Console.Error.WriteLine($"The project references template '{project.TemplateId}', which is not installed.");
            return 1;
        }

        if (command.Has("proof"))
        {
            project.ProofMode = true;
        }

        var output = command.Value("out") ?? Path.ChangeExtension(projectPath, ".pdf");

        var result = new CoverRenderer().Render(project, template, output);

        Console.WriteLine($"Wrote {result.OutputPath}");
        Console.WriteLine($"  Page: {result.PageSize}, trim {template.Trim}");

        foreach (var warning in result.Warnings)
        {
            Console.WriteLine("  ! " + warning);
        }

        if (result.Warnings.Count == 0)
        {
            Console.WriteLine("  No quality problems found.");
        }

        PrintTerms(template);
        PrintScaleReminder();
        return 0;
    }

    private static void PrintTerms(CoverTemplate template)
    {
        if (template.Terms is { Length: > 0 } terms)
        {
            Console.WriteLine();
            Console.WriteLine("  " + terms);
        }
    }

    private static void PrintScaleReminder()
    {
        Console.WriteLine();
        Console.WriteLine(
            "Print at 100% scale — do not let the print dialog 'fit to page', or the cover will not fit the case.");
    }

    /// <summary>Fills a template's slots from the art providers, matching kinds to slot shapes.</summary>
    private static async Task FillSlotsWithArtAsync(
        CoverProject project,
        CoverTemplate template,
        CommandLine command,
        ISteamDiscLogger logger)
    {
        var localDirectories = command.Value("art-dir") is { Length: > 0 } dir ? new[] { dir } : null;
        var resolver = ArtResolver.CreateDefault(command.Value("steamgriddb-key"), localDirectories, logger: logger);

        var requirements = template.Slots
            .Select(slot => new ArtRequirement(slot.Id, KindsForSlot(slot), slot.PreferredAspect))
            .ToList();

        Console.WriteLine("Fetching artwork...");

        try
        {
            var assets = await resolver
                .ResolveAsync(project.AppId, project.Title, requirements)
                .ConfigureAwait(false);

            foreach (var (slot, asset) in assets)
            {
                project.Artwork[slot] = asset.LocalPath;
                Console.WriteLine($"  {slot,-12} {asset.Candidate.ProviderId}  {asset.Candidate.Url}");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Console.WriteLine("  Could not reach the art providers; leaving slots empty.");
        }
    }

    /// <summary>
    /// Picks sensible art kinds for a slot, from what the slot <em>is</em> first and its shape
    /// second.
    /// </summary>
    /// <remarks>
    /// A case front and a case back are the same shape, so shape alone would ask for the same
    /// artwork twice and produce a cover with identical panels. Retail backs carry a wider,
    /// more scene-like image, so 'back' asks for a hero first and only falls back to the key art.
    /// </remarks>
    private static IReadOnlyList<ArtKind> KindsForSlot(CoverSlot slot)
    {
        switch (slot.Id.ToLowerInvariant())
        {
            case "front":
            case "label":
            case "insert":
                return new[] { ArtKind.Cover, ArtKind.Capsule, ArtKind.Hero };

            case "back":
                return new[] { ArtKind.Hero, ArtKind.Capsule, ArtKind.Header, ArtKind.Cover };

            case "spine":
            case "spineleft":
            case "spineright":
                return new[] { ArtKind.Logo };
        }

        var aspect = slot.Bounds.AspectRatio;

        if (slot.Circular)
        {
            return new[] { ArtKind.Cover, ArtKind.Capsule, ArtKind.Hero };
        }

        if (aspect < 0.4)
        {
            return new[] { ArtKind.Logo, ArtKind.Cover };
        }

        return aspect < 1.0
            ? new[] { ArtKind.Cover, ArtKind.Capsule, ArtKind.Hero }
            : new[] { ArtKind.Hero, ArtKind.Capsule, ArtKind.Header };
    }
}
