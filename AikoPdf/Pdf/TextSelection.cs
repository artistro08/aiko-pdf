using System.Text;
using Windows.Foundation;

namespace AikoPdf.Pdf;

/// <summary>
/// A drag selection over one page's glyphs.
///
/// The glyphs come in reading order, so the selection is the index range between the glyph under the point where
/// the drag started and the glyph under the pointer now. A press alone selects nothing; the range appears with the
/// first move. Everything is in rendered page coordinates (points, top-left origin) and independent of zoom.
/// </summary>
/// <remarks>
/// @author Devin Green (Artistro08)
/// </remarks>
public sealed class TextSelection
{
    // Lines this far apart vertically (in line heights) or misaligned horizontally belong to different blocks.
    private const double MaxLineGapInHeights   = 2.5;
    private const double MaxMisalignInHeights  = 2.0;

    private readonly IReadOnlyList<TextGlyph> glyphs;
    private readonly List<LineBand>            lines  = [];
    private readonly List<Block>               blocks = [];

    private Point? anchorPoint;
    private int    anchor = -1;
    private int    focus  = -1;

    /// <summary>Creates a selection over a page's glyphs. Glyphs must be in reading order with contiguous lines.</summary>
    /// <param name="glyphs">The page's glyphs, as produced by <see cref="PdfTextLayer"/>.</param>
    /// <param name="containers">
    /// Filled rectangles drawn on the page (card backgrounds, panels). A block of text inside one takes the whole
    /// rectangle as its area, so a drag through the card's padding stays in that card.
    /// </param>
    public TextSelection(IReadOnlyList<TextGlyph> glyphs, IReadOnlyList<Rect>? containers = null)
    {
        this.glyphs = glyphs;

        // Line bands: each line's index span and the vertical extent of all its glyphs, for hit testing and highlights.
        for (int i = 0; i < glyphs.Count; i++)
        {
            TextGlyph glyph = glyphs[i];
            if ((lines.Count == 0) || (lines[^1].LineIndex != glyph.LineIndex))
            {
                lines.Add(new LineBand(glyph.LineIndex, i, i, glyph.Bounds.Top, glyph.Bounds.Bottom, glyph.Bounds.Left, glyph.Bounds.Right));
                continue;
            }

            LineBand line = lines[^1];
            lines[^1] = line with
            {
                Last   = i,
                Top    = Math.Min(line.Top, glyph.Bounds.Top),
                Bottom = Math.Max(line.Bottom, glyph.Bounds.Bottom),
                Left   = Math.Min(line.Left, glyph.Bounds.Left),
                Right  = Math.Max(line.Right, glyph.Bounds.Right),
            };
        }

        // Blocks: runs of lines that follow each other closely and line up (left, right or centered), which is a
        // paragraph, a card or a column. Hit testing goes to a block first, so the empty space inside a card
        // belongs to that card and never to a neighbor whose text happens to be nearer.
        for (int i = 0; i < lines.Count; i++)
        {
            LineBand line = lines[i];
            if ((blocks.Count > 0) && Continues(lines[i - 1], line))
            {
                Block block = blocks[^1];
                blocks[^1]  = block with { LastLine = i, Bounds = Union(block.Bounds, line.Bounds) };
            }
            else
            {
                blocks.Add(new Block(i, i, line.Bounds));
            }
        }

        // A block drawn inside a panel owns the panel: the smallest one that holds all of its lines.
        if (containers is { Count: > 0 })
        {
            for (int i = 0; i < blocks.Count; i++)
            {
                Rect  bounds = blocks[i].Bounds;
                Rect? panel  = null;
                foreach (Rect container in containers)
                {
                    bool holds = (container.Left <= bounds.Left + 1) && (container.Top <= bounds.Top + 1)
                        && (container.Right >= bounds.Right - 1) && (container.Bottom >= bounds.Bottom - 1);
                    if (holds && ((panel is null) || (Area(container) < Area(panel.Value))))
                    {
                        panel = container;
                    }
                }

                if (panel is { } found)
                {
                    blocks[i] = blocks[i] with { Bounds = found, IsPanel = true };
                }
            }
        }
    }

    private static double Area(Rect rect) => rect.Width * rect.Height;

    private static bool Continues(LineBand previous, LineBand line)
    {
        double height  = Math.Max(previous.Bottom - previous.Top, line.Bottom - line.Top);
        double gap     = line.Top - previous.Bottom;
        bool   close   = Math.Abs(gap) <= (MaxLineGapInHeights * height);
        double slack   = MaxMisalignInHeights * height;
        bool   aligned = (Math.Abs(line.Left - previous.Left) <= slack)
            || (Math.Abs(line.Right - previous.Right) <= slack)
            || (Math.Abs(((line.Left + line.Right) / 2) - ((previous.Left + previous.Right) / 2)) <= slack);
        return close && aligned;
    }

    private static Rect Union(Rect a, Rect b)
    {
        double left   = Math.Min(a.Left, b.Left);
        double top    = Math.Min(a.Top, b.Top);
        double right  = Math.Max(a.Right, b.Right);
        double bottom = Math.Max(a.Bottom, b.Bottom);
        return new Rect(left, top, right - left, bottom - top);
    }

    /// <summary>True while nothing is selected.</summary>
    public bool IsEmpty => (anchor < 0) || (focus < 0);

    /// <summary>Index of the first selected glyph, or -1.</summary>
    public int Start => IsEmpty ? -1 : Math.Min(anchor, focus);

    /// <summary>Index of the last selected glyph, or -1.</summary>
    public int End => IsEmpty ? -1 : Math.Max(anchor, focus);

    /// <summary>Records where a drag started. Nothing is selected until <see cref="Extend"/> is called.</summary>
    /// <param name="point">The press position in rendered page coordinates.</param>
    public void Begin(Point point)
    {
        anchorPoint = point;
        anchor      = -1;
        focus       = -1;
    }

    /// <summary>Moves the far end of the selection to the pointer. No-op unless <see cref="Begin"/> was called.</summary>
    /// <param name="point">The pointer position in rendered page coordinates.</param>
    public void Extend(Point point)
    {
        if (anchorPoint is not { } start)
        {
            return;
        }

        if (anchor < 0)
        {
            anchor = HitTest(start);
        }

        focus = HitTest(point);

        // A pointer that wobbles within the letter it pressed on is still a click, not a one-letter selection.
        if ((focus == anchor) && (anchor >= 0) && glyphs[anchor].Bounds.Contains(point))
        {
            focus = -1;
        }
    }

    /// <summary>Selects every glyph on the page.</summary>
    public void SelectAll()
    {
        if (glyphs.Count == 0)
        {
            return;
        }

        anchorPoint = null;
        anchor      = 0;
        focus       = glyphs.Count - 1;
    }

    /// <summary>
    /// Selects the word under a point, the way a double-click does in any Windows text view. A point in the gap
    /// between words takes the nearer one.
    /// </summary>
    /// <param name="point">The click position in rendered page coordinates.</param>
    public void SelectWord(Point point)
    {
        int hit = HitTest(point);
        if (hit < 0)
        {
            return;
        }

        int word  = glyphs[hit].WordIndex;
        int first = hit;
        int last  = hit;
        while ((first > 0) && (glyphs[first - 1].WordIndex == word))
        {
            first--;
        }

        while ((last < glyphs.Count - 1) && (glyphs[last + 1].WordIndex == word))
        {
            last++;
        }

        anchorPoint = null;
        anchor      = first;
        focus       = last;
    }

    /// <summary>
    /// Selects the paragraph under a point, the way a triple-click does. A paragraph is narrower than the block
    /// hit testing uses: within the block, it runs over lines set at ordinary line spacing, in the same size of
    /// text, where every line but the last reaches the block's right edge. A blank line's worth of space, a change
    /// of size or a line that stops short (a heading, the end of a paragraph) closes it.
    /// </summary>
    /// <param name="point">The click position in rendered page coordinates.</param>
    public void SelectParagraph(Point point)
    {
        int hit = HitTest(point);
        if (hit < 0)
        {
            return;
        }

        foreach (Block block in blocks)
        {
            if ((hit < lines[block.FirstLine].First) || (hit > lines[block.LastLine].Last))
            {
                continue;
            }

            double right = 0;
            int    line  = block.FirstLine;
            for (int i = block.FirstLine; i <= block.LastLine; i++)
            {
                right = Math.Max(right, lines[i].Right);
                if ((hit >= lines[i].First) && (hit <= lines[i].Last))
                {
                    line = i;
                }
            }

            int top    = line;
            int bottom = line;
            while ((top > block.FirstLine) && SameParagraph(lines[top - 1], lines[top], right))
            {
                top--;
            }

            while ((bottom < block.LastLine) && SameParagraph(lines[bottom], lines[bottom + 1], right))
            {
                bottom++;
            }

            anchorPoint = null;
            anchor      = lines[top].First;
            focus       = lines[bottom].Last;
            return;
        }
    }

    /// <summary>True when a line runs on into the one below it as part of the same paragraph.</summary>
    /// <param name="upper">The line above.</param>
    /// <param name="lower">The line below.</param>
    /// <param name="right">The right edge of the block the lines sit in.</param>
    /// <returns>True when both lines belong to one paragraph.</returns>
    private static bool SameParagraph(LineBand upper, LineBand lower, double right)
    {
        // Measured on real documents: body text sits a quarter of a line height apart, airy card text about 0.8,
        // and a blank line between paragraphs leaves 1.5 or more.
        const double MaxGapInHeights = 1.2;

        // A line that wraps ends near the right edge, give or take a ragged word; one that stops this far short
        // ended its paragraph (a heading, a title, the last line).
        const double MaxShortfallInHeights = 4;

        double upperHeight = upper.Bottom - upper.Top;
        double lowerHeight = lower.Bottom - lower.Top;
        double height      = Math.Max(upperHeight, lowerHeight);
        double gap         = lower.Top - upper.Bottom;

        bool sameSize  = (height > 0) && ((Math.Min(upperHeight, lowerHeight) / height) >= 0.75);
        bool close     = (gap >= -(0.5 * height)) && (gap <= (MaxGapInHeights * height));
        bool runsOn    = upper.Right >= right - (MaxShortfallInHeights * height);
        return sameSize && close && runsOn;
    }

    /// <summary>Removes the selection.</summary>
    public void Clear()
    {
        anchorPoint = null;
        anchor      = -1;
        focus       = -1;
    }

    /// <summary>Builds the selected text, with a space between words and a line break between lines.</summary>
    /// <returns>The text, or an empty string when nothing is selected.</returns>
    public string GetText()
    {
        if (IsEmpty)
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        for (int i = Start; i <= End; i++)
        {
            TextGlyph glyph = glyphs[i];
            if (i > Start)
            {
                TextGlyph previous = glyphs[i - 1];
                if (previous.LineIndex != glyph.LineIndex)
                {
                    text.Append(Environment.NewLine);
                }
                else if (previous.WordIndex != glyph.WordIndex)
                {
                    text.Append(' ');
                }
            }

            text.Append(glyph.Text);
        }

        return text.ToString();
    }

    /// <summary>Builds one highlight rectangle per selected line, spanning the selected glyphs and the line's full height.</summary>
    /// <returns>The rectangles in rendered page coordinates; empty when nothing is selected.</returns>
    public IReadOnlyList<Rect> GetRects()
    {
        if (IsEmpty)
        {
            return [];
        }

        var rects = new List<Rect>();
        foreach (LineBand line in lines)
        {
            int first = Math.Max(line.First, Start);
            int last  = Math.Min(line.Last, End);
            if (first > last)
            {
                continue;
            }

            double left  = glyphs[first].Bounds.Left;
            double right = glyphs[last].Bounds.Right;
            rects.Add(new Rect(left, line.Top, Math.Max(0, right - left), line.Bottom - line.Top));
        }

        return rects;
    }

    /// <summary>
    /// The glyph a point lands on: the one under it, or on the nearest line the one closest horizontally. This is
    /// what makes dragging through margins and between lines feel like a text editor.
    /// </summary>
    /// <param name="point">A position in rendered page coordinates.</param>
    /// <returns>A glyph index, or -1 when the page has no text.</returns>
    public int HitTest(Point point)
    {
        if (glyphs.Count == 0)
        {
            return -1;
        }

        for (int i = 0; i < glyphs.Count; i++)
        {
            if (glyphs[i].Bounds.Contains(point))
            {
                return i;
            }
        }

        // The block that owns the point, then the nearest line inside it. A point inside a panel drawn on the page
        // belongs to that panel's text however near a neighbor's rows are, which is what keeps a drag through one
        // card's padding out of the card beside it. Everywhere else the nearest line decides, row before column,
        // the way a browser picks. Ranking whole blocks by their outline instead would let a full-width heading
        // claim the columns underneath it.
        Block? home      = null;
        double homeScore = double.MaxValue;
        foreach (Block block in blocks)
        {
            double score = LineDistance(block, point);
            if (block.IsPanel && block.Bounds.Contains(point))
            {
                // Below zero, so any panel beats any line; the smallest panel holding the point wins among them.
                double area = Area(block.Bounds);
                score = -1 + (area / (1 + area));
            }

            if (score < homeScore)
            {
                homeScore = score;
                home      = block;
            }
        }

        LineBand nearest = lines[home!.Value.FirstLine];
        double   bestDy  = double.MaxValue;
        double   bestDx  = double.MaxValue;
        for (int i = home.Value.FirstLine; i <= home.Value.LastLine; i++)
        {
            LineBand line = lines[i];
            double   dy   = (point.Y < line.Top) ? line.Top - point.Y : (point.Y > line.Bottom) ? point.Y - line.Bottom : 0;
            double   dx   = (point.X < line.Left) ? line.Left - point.X : (point.X > line.Right) ? point.X - line.Right : 0;
            if ((dy < bestDy) || ((dy == bestDy) && (dx < bestDx)))
            {
                bestDy  = dy;
                bestDx  = dx;
                nearest = line;
            }
        }

        if (point.X <= glyphs[nearest.First].Bounds.Left)
        {
            return nearest.First;
        }

        if (point.X >= glyphs[nearest.Last].Bounds.Right)
        {
            return nearest.Last;
        }

        int    best     = nearest.First;
        double bestDist = double.MaxValue;
        for (int i = nearest.First; i <= nearest.Last; i++)
        {
            Rect   bounds = glyphs[i].Bounds;
            double center = bounds.Left + (bounds.Width / 2);
            double dist   = Math.Abs(point.X - center);
            if (dist < bestDist)
            {
                bestDist = dist;
                best     = i;
            }
        }

        return best;
    }

    /// <summary>How far a point is from the nearest line of a block: the vertical gap first, the horizontal gap as a tie-break.</summary>
    private double LineDistance(Block block, Point point)
    {
        double best = double.MaxValue;
        for (int i = block.FirstLine; i <= block.LastLine; i++)
        {
            LineBand line = lines[i];
            double   dy   = (point.Y < line.Top) ? line.Top - point.Y : (point.Y > line.Bottom) ? point.Y - line.Bottom : 0;
            double   dx   = (point.X < line.Left) ? line.Left - point.X : (point.X > line.Right) ? point.X - line.Right : 0;
            best = Math.Min(best, (dy * 1000) + dx);
        }

        return best;
    }

    /// <summary>A text line's glyph index span and extent.</summary>
    private readonly record struct LineBand(int LineIndex, int First, int Last, double Top, double Bottom, double Left, double Right)
    {
        /// <summary>The line's extent as a rectangle.</summary>
        public Rect Bounds => new(Left, Top, Math.Max(0, Right - Left), Math.Max(0, Bottom - Top));
    }

    /// <summary>A run of lines that read as one paragraph, card or column, with their combined extent.</summary>
    private readonly record struct Block(int FirstLine, int LastLine, Rect Bounds, bool IsPanel = false);
}
