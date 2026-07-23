using SteamDisc.Core.Images;

namespace SteamDisc.Covers;

/// <param name="Template">A template that prints the finished sheet at its true size.</param>
/// <param name="Project">A project already bound to that template.</param>
/// <param name="Notes">What was recognised, and anything the user should check.</param>
public sealed record FinishedCoverResult(
    CoverTemplate Template,
    CoverProject Project,
    IReadOnlyList<string> Notes);

/// <summary>
/// Prepares a cover somebody else already finished for printing.
/// </summary>
/// <remarks>
/// <para>
/// The community gallery is full of completed covers — the artwork is already composited into
/// the template design, Steam header and legal text included. Those need no layout at all;
/// what they need is to be printed at exactly the right physical size, which is precisely what
/// a downloaded PNG does not tell a print dialog.
/// </para>
/// <para>
/// So this recognises the sheet by its pixel dimensions, binds it to the matching page
/// geometry, and hands back a project that renders it 1:1. The trim box comes along too, so
/// the user knows where to cut even though the image already carries registration marks.
/// </para>
/// </remarks>
public static class FinishedCoverImporter
{
    /// <summary>
    /// Sheet sizes in pixels at 300 DPI, mapped to the layout they belong to. Ambiguous sizes
    /// resolve to the most common format unless the caller says otherwise.
    /// </summary>
    private static readonly (int Width, int Height, CoverMedia Media, string Note)[] KnownSheets =
    {
        (3300, 2550, CoverMedia.DvdCase,
            "Letter landscape at 300 DPI. Both the DVD (Amaray) and Blu-ray case sheets are this size; " +
            "assuming DVD, which is what most community covers use. Pass --media blurayCase if it is a Blu-ray cover."),
        (1800, 1800, CoverMedia.DiscLabel, "Square sheet at 300 DPI — a disc face."),
        (1950, 1950, CoverMedia.JewelCase, "Jewel case tray card sheet at 300 DPI."),
        (1800, 2550, CoverMedia.Insert, "Portrait sheet at 300 DPI — a case insert."),
    };

    /// <param name="imagePath">A finished cover sheet, PNG or JPEG.</param>
    /// <param name="media">Override the recognised layout.</param>
    /// <param name="title">Title recorded on the project, for the file name and metadata.</param>
    public static FinishedCoverResult Prepare(string imagePath, CoverMedia? media = null, string? title = null)
    {
        if (!File.Exists(imagePath))
        {
            throw new FileNotFoundException("Cover image not found.", imagePath);
        }

        if (!RasterImage.IsSupported(imagePath))
        {
            throw new NotSupportedException($"'{Path.GetFileName(imagePath)}' is not a PNG or JPEG.");
        }

        var notes = new List<string>();
        var size = RasterImage.ReadSize(imagePath)
                   ?? throw new InvalidDataException($"Could not read the dimensions of '{imagePath}'.");

        var resolved = media ?? Recognise(size, notes) ?? CoverMedia.DvdCase;

        if (media is not null)
        {
            notes.Add($"Printing as a {resolved} cover, as requested.");
        }

        var reference = CoverTemplateCatalog.ForMedia(resolved);

        var template = new CoverTemplate
        {
            Id = "finished-" + resolved.ToString().ToLowerInvariant(),
            Name = $"Finished cover — {resolved}",
            Family = "Finished",
            Media = resolved,
            Page = reference.Page,
            Trim = reference.Trim,
            Bleed = 0,
            // The finished sheet carries the original template's own registration marks.
            DrawCropMarks = false,
            SourceDpi = CoverTemplateCatalog.SgcDpi,
            Source = reference.Source,
            Terms = reference.Terms,
            // The image is the whole sheet, so it goes in as the background rather than a slot.
            BackgroundPath = Path.GetFullPath(imagePath),
            RootPath = Path.GetDirectoryName(Path.GetFullPath(imagePath)),
        };

        // Background paths are resolved relative to the template folder; the image already
        // lives beside it, so reference it by name.
        template.BackgroundPath = Path.GetFileName(imagePath);

        var effectiveDpi = PrintUnits.EffectiveDpi(size.Width, template.Page.Width);
        notes.Add(
            $"{size.Width}×{size.Height} px across {template.Page} " +
            $"prints at about {effectiveDpi:F0} DPI.");

        if (effectiveDpi < CoverRenderer.MinimumAcceptableDpi)
        {
            notes.Add(
                $"That is below {CoverRenderer.MinimumAcceptableDpi:F0} DPI and will look soft. " +
                "Look for a higher-resolution version of this cover.");
        }

        notes.Add(
            $"Cut on the trim box: {template.Trim.Width:F1} × {template.Trim.Height:F1} mm, " +
            $"{template.Trim.X:F1} mm from the left and {template.Trim.Y:F1} mm from the top of the sheet.");

        var project = new CoverProject
        {
            TemplateId = template.Id,
            Title = title ?? Path.GetFileNameWithoutExtension(imagePath),
            CropMarks = false,
        };

        return new FinishedCoverResult(template, project, notes);
    }

    /// <summary>Matches a sheet's pixel size against the known 300 DPI layouts.</summary>
    internal static CoverMedia? Recognise((int Width, int Height) size, List<string> notes)
    {
        foreach (var (width, height, media, note) in KnownSheets)
        {
            // A couple of percent of slack covers re-saves that trimmed a row or two.
            if (Within(size.Width, width) && Within(size.Height, height))
            {
                notes.Add(note);
                return media;
            }
        }

        notes.Add(
            $"{size.Width}×{size.Height} px does not match a known sheet size. " +
            "Treating it as a DVD case cover; pass --media to correct that.");

        return null;
    }

    private static bool Within(int actual, int expected) => Math.Abs(actual - expected) <= expected * 0.02;
}
