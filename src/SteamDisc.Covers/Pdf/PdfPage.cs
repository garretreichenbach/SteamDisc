using System.Globalization;
using System.Text;
using SteamDisc.Core.Images;

namespace SteamDisc.Covers.Pdf;

/// <summary>
/// One page of a <see cref="PdfDocument"/>, with drawing in millimetres measured from the
/// top-left. PDF's own origin is bottom-left, and the flip happens here so that every caller
/// — templates, slots, crop marks — can think in the same coordinate system.
/// </summary>
public sealed class PdfPage
{
    private readonly StringBuilder _content = new();
    private readonly List<ImagePlacement> _images = new();

    internal PdfPage(SizeMm size) => Size = size;

    public SizeMm Size { get; }

    internal IReadOnlyList<ImagePlacement> Images => _images;

    /// <summary>Fills the whole page with a colour.</summary>
    public void FillPage(double r, double g, double b)
        => FillRect(new RectMm(0, 0, Size.Width, Size.Height), r, g, b);

    public void FillRect(RectMm rect, double r, double g, double b)
    {
        var (x, y) = ToPdf(rect.X, rect.Bottom);
        _content.Append(CultureInfo.InvariantCulture,
            $"q {r:0.###} {g:0.###} {b:0.###} rg {x:0.####} {y:0.####} " +
            $"{PrintUnits.MmToPoints(rect.Width):0.####} {PrintUnits.MmToPoints(rect.Height):0.####} re f Q\n");
    }

    /// <summary>
    /// Draws an image to exactly fill <paramref name="destination"/>, cropping as needed to
    /// preserve its aspect ratio according to <paramref name="fit"/>.
    /// </summary>
    public void DrawImage(RasterImage image, RectMm destination, SlotFit fit, double rotationDegrees = 0)
    {
        var name = $"Im{_images.Count + 1}";
        _images.Add(new ImagePlacement(name, image));

        var placement = ComputePlacement(image, destination, fit);

        _content.Append("q\n");

        // Clip to the destination so a Cover fit cannot bleed into a neighbouring panel.
        var (clipX, clipY) = ToPdf(destination.X, destination.Bottom);
        _content.Append(CultureInfo.InvariantCulture,
            $"{clipX:0.####} {clipY:0.####} {PrintUnits.MmToPoints(destination.Width):0.####} " +
            $"{PrintUnits.MmToPoints(destination.Height):0.####} re W n\n");

        if (Math.Abs(rotationDegrees) > 0.001)
        {
            // Rotate about the centre of the destination rectangle.
            var centreX = PrintUnits.MmToPoints(destination.X + (destination.Width / 2));
            var centreY = PrintUnits.MmToPoints(Size.Height - (destination.Y + (destination.Height / 2)));
            var radians = rotationDegrees * Math.PI / 180.0;
            var cos = Math.Cos(radians);
            var sin = Math.Sin(radians);

            _content.Append(CultureInfo.InvariantCulture, $"1 0 0 1 {centreX:0.####} {centreY:0.####} cm\n");
            _content.Append(CultureInfo.InvariantCulture, $"{cos:0.######} {sin:0.######} {-sin:0.######} {cos:0.######} 0 0 cm\n");
            _content.Append(CultureInfo.InvariantCulture, $"1 0 0 1 {-centreX:0.####} {-centreY:0.####} cm\n");
        }

        var (drawX, drawY) = ToPdf(placement.X, placement.Bottom);
        _content.Append(CultureInfo.InvariantCulture,
            $"{PrintUnits.MmToPoints(placement.Width):0.####} 0 0 {PrintUnits.MmToPoints(placement.Height):0.####} " +
            $"{drawX:0.####} {drawY:0.####} cm\n");
        _content.Append(CultureInfo.InvariantCulture, $"/{name} Do\nQ\n");
    }

    /// <summary>
    /// Draws text within a rectangle, aligned along the box's own axis.
    /// </summary>
    /// <remarks>
    /// Rotation is handled by laying the text out in the rotated frame and then rotating about
    /// the centre of <paramref name="bounds"/>. That is what a spine caption needs: a quarter
    /// turn swaps which dimension the text runs along, so aligning against the unrotated width
    /// would centre a spine title against 12 mm instead of the 174 mm it actually runs down.
    /// </remarks>
    public void DrawText(
        string text,
        RectMm bounds,
        double fontSizePoints,
        (double R, double G, double B) color,
        string align = "left",
        double rotationDegrees = 0)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var rotated = Math.Abs(rotationDegrees) > 0.001;
        var quarterTurn = rotated && Math.Abs(Math.Cos(rotationDegrees * Math.PI / 180.0)) < 0.001;

        // The axis the text runs along, in points.
        var runLength = PrintUnits.MmToPoints(quarterTurn ? bounds.Height : bounds.Width);

        // Helvetica's average advance is about half its point size. Good enough for the short
        // strings covers use, and it avoids embedding font metrics for a single caption.
        var textWidth = text.Length * fontSizePoints * 0.5;

        var offset = align.ToLowerInvariant() switch
        {
            "center" or "centre" => -textWidth / 2,
            "right" => (runLength / 2) - textWidth,
            _ => -runLength / 2,
        };

        // Nudge the baseline down by roughly half the cap height so the glyphs sit centred
        // across the box's short axis rather than resting on its centre line.
        const double capHeightFraction = 0.35;
        var baselineOffset = -fontSizePoints * capHeightFraction;

        var (centreX, centreY) = ToPdf(
            bounds.X + (bounds.Width / 2),
            bounds.Y + (bounds.Height / 2));

        _content.Append("q\n");
        _content.Append(CultureInfo.InvariantCulture, $"1 0 0 1 {centreX:0.####} {centreY:0.####} cm\n");

        if (rotated)
        {
            var radians = rotationDegrees * Math.PI / 180.0;
            var cos = Math.Cos(radians);
            var sin = Math.Sin(radians);
            _content.Append(CultureInfo.InvariantCulture,
                $"{cos:0.######} {sin:0.######} {-sin:0.######} {cos:0.######} 0 0 cm\n");
        }

        _content.Append(CultureInfo.InvariantCulture,
            $"BT /F1 {fontSizePoints:0.###} Tf {color.R:0.###} {color.G:0.###} {color.B:0.###} rg " +
            $"{offset:0.####} {baselineOffset:0.####} Td ({EscapeText(text)}) Tj ET\nQ\n");
    }

    /// <summary>
    /// Draws crop marks at the corners of <paramref name="trim"/>, so a print shop knows where
    /// to cut. The marks sit outside the trim box and stop short of it, leaving the cover itself
    /// unmarked.
    /// </summary>
    public void DrawCropMarks(RectMm trim, double markLengthMm = 4, double offsetMm = 1)
    {
        if (trim.Width <= 0 || trim.Height <= 0)
        {
            return;
        }

        var left = trim.X;
        var top = trim.Y;
        var right = trim.Right;
        var bottom = trim.Bottom;

        _content.Append("q 0 0 0 RG 0.25 w\n");

        foreach (var (x, y, horizontal) in new[]
                 {
                     (left, top, true), (left, top, false),
                     (right, top, true), (right, top, false),
                     (left, bottom, true), (left, bottom, false),
                     (right, bottom, true), (right, bottom, false),
                 })
        {
            var towardsInside = x <= left ? -1 : 1;
            var verticalDirection = y <= top ? -1 : 1;

            var (x1, y1, x2, y2) = horizontal
                ? (x + (towardsInside * offsetMm), y, x + (towardsInside * (offsetMm + markLengthMm)), y)
                : (x, y + (verticalDirection * offsetMm), x, y + (verticalDirection * (offsetMm + markLengthMm)));

            var (px1, py1) = ToPdf(x1, y1);
            var (px2, py2) = ToPdf(x2, y2);
            _content.Append(CultureInfo.InvariantCulture,
                $"{px1:0.####} {py1:0.####} m {px2:0.####} {py2:0.####} l S\n");
        }

        _content.Append("Q\n");
    }

    /// <summary>Outlines a rectangle, used to show slot boundaries on a proof sheet.</summary>
    public void StrokeRect(RectMm rect, double r, double g, double b, double widthPoints = 0.5)
    {
        var (x, y) = ToPdf(rect.X, rect.Bottom);
        _content.Append(CultureInfo.InvariantCulture,
            $"q {r:0.###} {g:0.###} {b:0.###} RG {widthPoints:0.###} w {x:0.####} {y:0.####} " +
            $"{PrintUnits.MmToPoints(rect.Width):0.####} {PrintUnits.MmToPoints(rect.Height):0.####} re S Q\n");
    }

    internal string BuildContentStream() => _content.ToString();

    /// <summary>Converts top-left millimetres to PDF's bottom-left points.</summary>
    private (double X, double Y) ToPdf(double xMm, double yMmFromTop)
        => (PrintUnits.MmToPoints(xMm), PrintUnits.MmToPoints(Size.Height - yMmFromTop));

    /// <summary>
    /// Works out where to draw an image so that it fills or fits the destination while keeping
    /// its aspect ratio. Returns a rectangle that may extend beyond the destination; the caller
    /// clips.
    /// </summary>
    internal static RectMm ComputePlacement(RasterImage image, RectMm destination, SlotFit fit)
    {
        if (fit == SlotFit.Stretch || image.AspectRatio <= 0)
        {
            return destination;
        }

        var destinationAspect = destination.AspectRatio;
        var imageAspect = image.AspectRatio;

        var fillWider = fit == SlotFit.Cover
            ? imageAspect < destinationAspect
            : imageAspect > destinationAspect;

        if (fillWider)
        {
            var width = destination.Width;
            var height = width / imageAspect;
            return new RectMm(destination.X, destination.Y + ((destination.Height - height) / 2), width, height);
        }
        else
        {
            var height = destination.Height;
            var width = height * imageAspect;
            return new RectMm(destination.X + ((destination.Width - width) / 2), destination.Y, width, height);
        }
    }

    private static string EscapeText(string text)
    {
        var builder = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            switch (c)
            {
                case '(':
                case ')':
                case '\\':
                    builder.Append('\\').Append(c);
                    break;
                default:
                    // Anything outside Latin-1 has no glyph in the standard encoding.
                    builder.Append(c <= 0xFF ? c : '?');
                    break;
            }
        }

        return builder.ToString();
    }

    internal readonly record struct ImagePlacement(string ResourceName, RasterImage Image);
}
