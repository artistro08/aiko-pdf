using Windows.Foundation;

namespace AikoPdf.Pdf;

/// <summary>
/// One visible character on a page, positioned in rendered page coordinates: points (1/72 inch), origin at the
/// top-left corner, after the page's rotation has been applied. Glyphs are listed in reading order, so a selection
/// is a contiguous index range.
/// </summary>
/// <param name="Text">The character (one code point, occasionally a ligature).</param>
/// <param name="Bounds">Where the character sits on the page, in points, top-left origin.</param>
/// <param name="WordIndex">Index of the word this glyph belongs to; a change between neighbors means a space.</param>
/// <param name="LineIndex">Index of the text line this glyph belongs to; a change between neighbors means a line break.</param>
/// <remarks>
/// @author Devin Green (Artistro08)
/// </remarks>
public readonly record struct TextGlyph(string Text, Rect Bounds, int WordIndex, int LineIndex);
