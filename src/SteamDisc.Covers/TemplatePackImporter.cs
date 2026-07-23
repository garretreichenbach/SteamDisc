using System.IO.Compression;
using SteamDisc.Core.Images;

namespace SteamDisc.Covers;

/// <param name="Template">The template that was created.</param>
/// <param name="Directory">Where it was installed.</param>
/// <param name="Notes">Anything the user should know about how it was interpreted.</param>
public sealed record TemplateImportResult(CoverTemplate Template, string Directory, IReadOnlyList<string> Notes);

/// <summary>
/// Imports cover template artwork the user has downloaded and binds it to the matching
/// geometry, producing a template the renderer can use.
/// </summary>
/// <remarks>
/// <para>
/// The Steam Game Covers templates are the motivating case: 300 DPI sheets for DVD (Amaray),
/// Blu-ray, jewel case and disc, in an official Steam style plus community variants.
/// </para>
/// <para>
/// None of that artwork is downloaded or bundled. The user fetches a pack themselves and
/// points the Builder at it; this recognises the layout, applies the measured geometry, and
/// records the source in the template's metadata so a printed cover can be attributed. The
/// design is composited <em>over</em> the user's art, which is what keeps the Steam header and
/// the website and legal text intact as the templates' terms require.
/// </para>
/// <para>
/// PSDs are not parsed. Every pack ships a PNG or JPG of the same design, and that is what
/// gets used.
/// </para>
/// </remarks>
public static class TemplatePackImporter
{
    /// <summary>
    /// Recognised layouts, matched against a downloaded file's name. Ordered most specific
    /// first, since "SGC_BLURAY_INSERT" also contains "BLURAY".
    /// </summary>
    private static readonly (string[] Markers, Func<CoverTemplate> Factory)[] KnownLayouts =
    {
        (new[] { "bluray_insert", "blu-ray_insert", "bluray insert" }, CoverTemplateCatalog.SgcBluRayInsert),
        (new[] { "amary_insert", "amaray_insert", "dvd_insert", "dvd insert" }, CoverTemplateCatalog.SgcDvdInsert),
        (new[] { "jewel_front", "jewel front" }, CoverTemplateCatalog.SgcJewelFront),
        (new[] { "jewel_back", "jewel back", "jewel_tray" }, CoverTemplateCatalog.SgcJewelBack),
        // "Amaray" is the standard DVD keep case; the templates spell it AMARY.
        (new[] { "amary", "amaray", "dvd" }, CoverTemplateCatalog.SgcDvdCase),
        (new[] { "bluray", "blu-ray", "blu_ray" }, CoverTemplateCatalog.SgcBluRayCase),
        (new[] { "jewel", "cd_case" }, CoverTemplateCatalog.SgcJewelFront),
        (new[] { "disc", "label" }, CoverTemplateCatalog.SgcDiscLabel),
    };

    /// <summary>Fallback mapping when no specific layout matches.</summary>
    private static readonly (string Marker, CoverMedia Media)[] MediaMarkers =
    {
        ("bluray", CoverMedia.BluRayCase),
        ("blu-ray", CoverMedia.BluRayCase),
        ("amary", CoverMedia.DvdCase),
        ("amaray", CoverMedia.DvdCase),
        ("dvd", CoverMedia.DvdCase),
        ("jewel", CoverMedia.JewelCase),
        ("insert", CoverMedia.Insert),
        ("disc", CoverMedia.DiscLabel),
        ("label", CoverMedia.DiscLabel),
    };

    /// <summary>
    /// Imports one artwork file as a template overlay, matching it to a known layout by name
    /// and taking the geometry from there.
    /// </summary>
    /// <param name="artworkPath">A PNG or JPG downloaded from a template source.</param>
    /// <param name="destinationRoot">Template library root; a subfolder is created.</param>
    /// <param name="media">Case type, or null to work it out from the file name.</param>
    /// <param name="attribution">Author or source to record.</param>
    /// <param name="family">Design family, e.g. "Steam" or "uPlay".</param>
    public static TemplateImportResult ImportArtwork(
        string artworkPath,
        string? destinationRoot = null,
        CoverMedia? media = null,
        string? attribution = null,
        string? family = null)
    {
        if (!File.Exists(artworkPath))
        {
            throw new FileNotFoundException("Template artwork not found.", artworkPath);
        }

        var notes = new List<string>();
        var fileName = Path.GetFileNameWithoutExtension(artworkPath);

        if (Path.GetExtension(artworkPath).Equals(".psd", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                "PSD templates cannot be read directly. Every pack ships a PNG or JPG of the same design — " +
                "import that, or flatten the PSD to PNG first.");
        }

        if (!RasterImage.IsSupported(artworkPath))
        {
            throw new NotSupportedException($"'{Path.GetFileName(artworkPath)}' is not a PNG or JPEG.");
        }

        var template = ResolveLayout(fileName, media, notes);

        var size = RasterImage.ReadSize(artworkPath);
        if (size is { } dimensions)
        {
            VerifyAgainstGeometry(template, dimensions, notes);
        }

        // An official sheet replaces the built-in entry for its layout, so the picker does not
        // offer the same design twice. Anything else keeps an id of its own: two people's DVD
        // designs are two templates that happen to share a geometry, not one template.
        var isOfficialSheet = fileName.StartsWith("sgc", StringComparison.OrdinalIgnoreCase) &&
                              template.Family == "Steam" &&
                              media is null &&
                              family is null;

        var id = isOfficialSheet ? template.Id : MakeId(fileName, template.Media);

        var directory = Path.Combine(destinationRoot ?? CoverTemplateCatalog.DefaultUserTemplateDirectory, id);
        Directory.CreateDirectory(directory);

        var overlayName = "overlay" + Path.GetExtension(artworkPath).ToLowerInvariant();
        File.Copy(artworkPath, Path.Combine(directory, overlayName), overwrite: true);

        template.Id = id;
        template.Name = isOfficialSheet ? template.Name : PrettyName(fileName);
        template.OverlayPath = overlayName;
        template.RootPath = directory;

        if (family is { Length: > 0 })
        {
            template.Family = family;
        }

        if (attribution is { Length: > 0 })
        {
            template.Author = attribution;
            template.Source = attribution;
        }

        template.Save(Path.Combine(directory, CoverTemplate.FileName));

        notes.Add(
            "The design is composited over your artwork, so its frame, branding and legal text stay intact.");

        notes.Add(
            "Blank template sheets also carry dashed guides and instructional text, which an artist " +
            "would erase by hand before finalising. Those come along too. Erase them in an image " +
            "editor and re-import if you want a clean print, or use 'covers print' for a cover that " +
            "somebody has already finished.");

        return new TemplateImportResult(template, directory, notes);
    }

    /// <summary>
    /// Imports every artwork file in a folder or zip — the shape a downloaded pack arrives in.
    /// </summary>
    public static IReadOnlyList<TemplateImportResult> ImportPack(
        string packPath,
        string? destinationRoot = null,
        string? attribution = null,
        string? family = null)
    {
        var results = new List<TemplateImportResult>();

        if (File.Exists(packPath) && Path.GetExtension(packPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var extracted = Path.Combine(Path.GetTempPath(), "steamdisc-pack-" + Guid.NewGuid().ToString("N"));
            try
            {
                ZipFile.ExtractToDirectory(packPath, extracted);
                results.AddRange(ImportPack(extracted, destinationRoot, attribution, family));
            }
            finally
            {
                if (Directory.Exists(extracted))
                {
                    Directory.Delete(extracted, recursive: true);
                }
            }

            return results;
        }

        if (!Directory.Exists(packPath))
        {
            throw new DirectoryNotFoundException($"'{packPath}' is not a folder or a zip file.");
        }

        foreach (var file in Directory
                     .EnumerateFiles(packPath, "*", SearchOption.AllDirectories)
                     .Where(RasterImage.IsSupported)
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                results.Add(ImportArtwork(file, destinationRoot, media: null, attribution, family));
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or IOException)
            {
                // One unusable file should not abandon the rest of the pack.
            }
        }

        if (results.Count == 0)
        {
            throw new InvalidOperationException(
                $"No PNG or JPEG templates were found in '{packPath}'. " +
                "If the pack contains only PSDs, flatten one to PNG and import that.");
        }

        return results;
    }

    /// <summary>Picks the layout for a downloaded file, honouring an explicit media override.</summary>
    internal static CoverTemplate ResolveLayout(string fileName, CoverMedia? media, List<string> notes)
    {
        if (media is { } explicitMedia)
        {
            return CoverTemplateCatalog.ForMedia(explicitMedia);
        }

        var lower = fileName.ToLowerInvariant();

        foreach (var (markers, factory) in KnownLayouts)
        {
            if (markers.Any(m => lower.Contains(m, StringComparison.Ordinal)))
            {
                var template = factory();
                notes.Add($"Recognised '{fileName}' as {template.Name}.");
                return template;
            }
        }

        var inferred = InferMedia(fileName) ?? CoverMedia.Custom;
        notes.Add(
            $"'{fileName}' did not match a known layout; treating it as a {inferred} template. " +
            "Pass --media to override.");

        return CoverTemplateCatalog.ForMedia(inferred);
    }

    /// <summary>
    /// Sanity-checks the artwork's pixel size against the geometry we are about to apply, so a
    /// mismatched or resized template is reported rather than silently printing wrong.
    /// </summary>
    private static void VerifyAgainstGeometry(
        CoverTemplate template,
        (int Width, int Height) dimensions,
        List<string> notes)
    {
        var dpi = template.SourceDpi ?? CoverTemplateCatalog.SgcDpi;
        var expectedWidth = (int)Math.Round(PrintUnits.MmToPixels(template.Page.Width, dpi));
        var expectedHeight = (int)Math.Round(PrintUnits.MmToPixels(template.Page.Height, dpi));

        notes.Add(
            $"Artwork is {dimensions.Width}×{dimensions.Height} px; the layout expects " +
            $"{expectedWidth}×{expectedHeight} px at {dpi:F0} DPI ({template.Page}).");

        var widthOff = Math.Abs(dimensions.Width - expectedWidth) / (double)expectedWidth;
        var heightOff = Math.Abs(dimensions.Height - expectedHeight) / (double)expectedHeight;

        if (widthOff > 0.02 || heightOff > 0.02)
        {
            var actualDpi = PrintUnits.EffectiveDpi(dimensions.Width, template.Page.Width);
            notes.Add(
                $"That is a {Math.Max(widthOff, heightOff):P0} difference. The design may have been resized, " +
                $"or this may be a different layout. It works out to about {actualDpi:F0} DPI across the page — " +
                "check the printed proof before committing to a full run.");
        }

        var effectiveDpi = PrintUnits.EffectiveDpi(dimensions.Width, template.Page.Width);
        if (effectiveDpi < CoverRenderer.MinimumAcceptableDpi)
        {
            notes.Add(
                $"At {effectiveDpi:F0} DPI this design will look soft in print. Look for a higher-resolution version.");
        }
    }

    internal static CoverMedia? InferMedia(string fileName)
    {
        var lower = fileName.ToLowerInvariant();

        // "insert" only qualifies the case type, so it is checked after the more specific
        // markers have had a chance to match.
        foreach (var (marker, media) in MediaMarkers.Where(m => m.Marker != "insert"))
        {
            if (lower.Contains(marker, StringComparison.Ordinal))
            {
                return lower.Contains("insert", StringComparison.Ordinal) ? CoverMedia.Insert : media;
            }
        }

        return lower.Contains("insert", StringComparison.Ordinal) ? CoverMedia.Insert : null;
    }

    private static string MakeId(string fileName, CoverMedia media)
    {
        var stem = new string(fileName
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray())
            .Trim('-');

        while (stem.Contains("--", StringComparison.Ordinal))
        {
            stem = stem.Replace("--", "-", StringComparison.Ordinal);
        }

        if (stem.Length == 0)
        {
            stem = "template";
        }

        return $"{stem}-{media.ToString().ToLowerInvariant()}";
    }

    private static string PrettyName(string fileName)
    {
        var cleaned = fileName.Replace('_', ' ').Replace('-', ' ').Trim();
        return cleaned.Length == 0 ? "Imported template" : cleaned;
    }
}
