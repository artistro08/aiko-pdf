using Windows.Foundation;

namespace AikoPdf.Pdf;

/// <summary>
/// Converts rectangles from PDF page space (origin bottom-left, y up) to the space the viewer works in (origin
/// top-left, y down), both in points. Rotation is already applied by the text parser, so only the flip and the
/// page origin offset are handled here.
/// </summary>
/// <remarks>
/// @author Devin Green (Artistro08)
/// @link https://opensource.adobe.com/dc-acrobat-sdk-docs/pdfstandards/PDF32000_2008.pdf (section 8.3, coordinate systems)
/// </remarks>
public readonly struct PageGeometry
{
    private readonly double originX;
    private readonly double top;

    /// <summary>Describes a page from the bounds of its visible area in page space.</summary>
    /// <param name="left">Left edge of the visible area.</param>
    /// <param name="bottom">Bottom edge of the visible area.</param>
    /// <param name="right">Right edge of the visible area.</param>
    /// <param name="top">Top edge of the visible area.</param>
    public PageGeometry(double left, double bottom, double right, double top)
    {
        originX      = left;
        this.top     = top;
        RenderedSize = new Size(right - left, top - bottom);
    }

    /// <summary>The page size in points.</summary>
    public Size RenderedSize { get; }

    /// <summary>Maps a rectangle from page space to viewer space. Edges may be given in any order.</summary>
    /// <param name="x1">One horizontal edge.</param>
    /// <param name="y1">One vertical edge.</param>
    /// <param name="x2">The other horizontal edge.</param>
    /// <param name="y2">The other vertical edge.</param>
    /// <returns>The same area with a top-left origin.</returns>
    public Rect ToRendered(double x1, double y1, double x2, double y2)
    {
        double left   = Math.Min(x1, x2) - originX;
        double width  = Math.Abs(x2 - x1);
        double height = Math.Abs(y2 - y1);
        double y      = top - Math.Max(y1, y2);

        return new Rect(left, y, width, height);
    }
}
