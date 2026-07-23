using SteamDisc.Core.Images;
using SteamDisc.Core.Theming;
using SteamDisc.Covers.Pdf;

namespace SteamDisc.Covers;

/// <param name="OutputPath">Where the PDF was written.</param>
/// <param name="PageSize">Page size.</param>
/// <param name="Warnings">Quality problems worth telling the user about before they print.</param>
public sealed record CoverRenderResult(string OutputPath, SizeMm PageSize, IReadOnlyList<string> Warnings);

/// <summary>
/// Composites a <see cref="CoverProject"/> onto its template and writes a print-ready PDF.
/// </summary>
/// <remarks>
/// Draw order is background, then the user's art per slot, then the template overlay, then
/// text. That ordering is what lets a downloaded design keep its frame, branding and legal
/// text on top while the key art underneath is the user's own — which is exactly what the
/// templates' terms require.
/// </remarks>
public sealed class CoverRenderer
{
    /// <summary>Below this, a cover looks visibly soft in print.</summary>
    public const double MinimumAcceptableDpi = 200;

    /// <summary>The resolution the template sources ask for.</summary>
    public const double PreferredDpi = 300;

    public CoverRenderResult Render(CoverProject project, CoverTemplate template, string outputPath)
    {
        var problems = template.Validate();
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "The cover template is not usable:" + Environment.NewLine +
                string.Join(Environment.NewLine, problems.Select(p => "  " + p)));
        }

        var warnings = new List<string>();
        var document = new PdfDocument();
        var page = document.AddPage(template.Page);

        // White, so an unfilled area prints as paper rather than as whatever the printer guesses.
        page.FillPage(1, 1, 1);

        if (template.ResolveAsset(template.BackgroundPath) is { } background)
        {
            DrawFullPage(page, background, template, warnings);
        }

        var tokens = project.BuildTokens();

        foreach (var slot in template.Slots)
        {
            var artworkPath = project.ResolveArtwork(slot.Id);
            if (artworkPath is null)
            {
                if (project.Artwork.ContainsKey(slot.Id))
                {
                    warnings.Add($"Artwork for '{slot.Id}' could not be found and was skipped.");
                }

                continue;
            }

            RasterImage image;
            try
            {
                image = RasterImage.Load(artworkPath);
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or IOException)
            {
                warnings.Add($"Artwork for '{slot.Id}' could not be read: {ex.Message}");
                continue;
            }

            // Slot bounds are already in page coordinates. Art in a slot that reaches the trim
            // edge is grown into the bleed, so a slightly off cut cannot expose white paper.
            var bounds = ExtendIntoBleed(slot.Bounds, template);

            page.DrawImage(image, bounds, slot.Fit, slot.Rotation);

            var effectiveDpi = Math.Min(
                PrintUnits.EffectiveDpi(image.Width, bounds.Width),
                PrintUnits.EffectiveDpi(image.Height, bounds.Height));

            if (effectiveDpi < MinimumAcceptableDpi)
            {
                warnings.Add(
                    $"Artwork for '{slot.Id}' prints at about {effectiveDpi:F0} DPI " +
                    $"({image.Width}×{image.Height} px across {bounds.Width:F0}×{bounds.Height:F0} mm). " +
                    $"Aim for {PreferredDpi:F0} DPI; below {MinimumAcceptableDpi:F0} it will look soft.");
            }
        }

        if (template.ResolveAsset(template.OverlayPath) is { } overlay)
        {
            DrawFullPage(page, overlay, template, warnings);
        }

        foreach (var field in template.TextFields)
        {
            var text = project.Text.TryGetValue(field.Id, out var custom) ? custom : field.Text;
            text = ThemeStrings.Format(text, tokens);

            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            page.DrawText(text, field.Bounds, field.FontSize, ToRgb(field.Color), field.Align, field.Rotation);
        }

        if (project.ProofMode)
        {
            foreach (var slot in template.Slots)
            {
                page.StrokeRect(slot.Bounds, 1, 0, 1);
            }

            page.StrokeRect(template.Trim, 0, 0.6, 1);
        }

        // Only add crop marks when the template does not already carry registration marks.
        if (project.CropMarks && template.DrawCropMarks)
        {
            page.DrawCropMarks(template.Trim);
        }

        document.Save(outputPath);
        return new CoverRenderResult(outputPath, template.Page, warnings);
    }

    private static void DrawFullPage(
        PdfPage page,
        string imagePath,
        CoverTemplate template,
        List<string> warnings)
    {
        try
        {
            var image = RasterImage.Load(imagePath);
            page.DrawImage(image, new RectMm(0, 0, template.Page.Width, template.Page.Height), SlotFit.Stretch);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or IOException)
        {
            warnings.Add($"Template artwork '{Path.GetFileName(imagePath)}' could not be read: {ex.Message}");
        }
    }

    /// <summary>Grows a slot outward on any side that sits on the trim edge.</summary>
    private static RectMm ExtendIntoBleed(RectMm bounds, CoverTemplate template)
    {
        if (template.Bleed <= 0)
        {
            return bounds;
        }

        const double tolerance = 0.05;
        var trim = template.Trim;
        var bleed = template.Bleed;

        var left = Math.Abs(bounds.X - trim.X) < tolerance ? bleed : 0;
        var top = Math.Abs(bounds.Y - trim.Y) < tolerance ? bleed : 0;
        var right = Math.Abs(bounds.Right - trim.Right) < tolerance ? bleed : 0;
        var bottom = Math.Abs(bounds.Bottom - trim.Bottom) < tolerance ? bleed : 0;

        return new RectMm(
            bounds.X - left,
            bounds.Y - top,
            bounds.Width + left + right,
            bounds.Height + top + bottom);
    }

    private static (double R, double G, double B) ToRgb(string hex)
        => ThemeColor.TryParse(hex, out var color)
            ? (color.R / 255.0, color.G / 255.0, color.B / 255.0)
            : (1, 1, 1);

    /// <summary>
    /// Checks a project against its template without rendering, so a preview can warn about
    /// missing or low-resolution art as the user picks it.
    /// </summary>
    public static IReadOnlyList<string> Inspect(CoverProject project, CoverTemplate template)
    {
        var warnings = new List<string>();

        foreach (var slot in template.Slots)
        {
            var path = project.ResolveArtwork(slot.Id);
            if (path is null)
            {
                warnings.Add($"Slot '{slot.Label}' ({slot.Id}) has no artwork.");
                continue;
            }

            var size = RasterImage.ReadSize(path);
            if (size is null)
            {
                warnings.Add($"Artwork for '{slot.Id}' is not a readable PNG or JPEG.");
                continue;
            }

            var dpi = Math.Min(
                PrintUnits.EffectiveDpi(size.Value.Width, slot.Bounds.Width),
                PrintUnits.EffectiveDpi(size.Value.Height, slot.Bounds.Height));

            if (dpi < MinimumAcceptableDpi)
            {
                warnings.Add($"Artwork for '{slot.Id}' prints at about {dpi:F0} DPI; aim for {PreferredDpi:F0}.");
            }
        }

        return warnings;
    }
}
