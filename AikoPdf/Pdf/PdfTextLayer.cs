using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Graphics;
using UglyToad.PdfPig.Outline;
using Windows.Foundation;

namespace AikoPdf.Pdf;

/// <summary>
/// The text behind a rendered page: every visible character with its position, in reading order, so the viewer
/// can select and copy text from what is otherwise a bitmap.
///
/// Windows renders the pages but exposes no text, so this parses the same file with PdfPig. Glyph lists are built
/// on first use per page and cached for the life of the document. PdfPig documents are not thread-safe, so all
/// access is serialized behind one lock.
/// </summary>
/// <remarks>
/// @author Devin Green (Artistro08)
/// @link https://github.com/UglyToad/PdfPig
/// </remarks>
public sealed class PdfTextLayer : IDisposable
{
    private readonly PdfDocument                              document;
    private readonly Lock                                     gate  = new();
    private readonly Dictionary<int, IReadOnlyList<TextGlyph>> cache      = [];
    private readonly Dictionary<int, IReadOnlyList<Rect>>      containers = [];
    private readonly Dictionary<int, string>                   pageNames  = [];

    private PdfTextLayer(PdfDocument document)
    {
        this.document = document;

        // The outline (bookmarks) is the closest thing a PDF has to page names: the first entry that points at a
        // page names that page.
        if (document.TryGetBookmarks(out Bookmarks? bookmarks))
        {
            foreach (DocumentBookmarkNode node in bookmarks.GetNodes().OfType<DocumentBookmarkNode>())
            {
                if (!string.IsNullOrWhiteSpace(node.Title))
                {
                    pageNames.TryAdd(node.PageNumber, node.Title.Trim());
                }
            }
        }
    }

    /// <summary>Number of pages in the document.</summary>
    public int PageCount => document.NumberOfPages;

    /// <summary>Opens a PDF for text extraction.</summary>
    /// <param name="path">Full path of the file.</param>
    /// <param name="password">The user password for an encrypted file, or null.</param>
    /// <returns>The open text layer.</returns>
    /// <exception cref="UglyToad.PdfPig.Exceptions.PdfDocumentEncryptedException">The file needs a password, or the one given is wrong.</exception>
    public static PdfTextLayer Open(string path, string? password = null)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The PDF file was not found.", path);
        }

        var options = new ParsingOptions
        {
            UseLenientParsing = true,
            SkipMissingFonts  = true,
        };
        if (password is not null)
        {
            options.Password = password;
        }

        return new PdfTextLayer(PdfDocument.Open(path, options));
    }

    /// <summary>The name the document's outline gives a page, or null when no bookmark points at it.</summary>
    /// <param name="pageNumber">1-based page number.</param>
    /// <returns>The bookmark title, or null.</returns>
    public string? GetPageName(int pageNumber)
        => pageNames.GetValueOrDefault(pageNumber);

    /// <summary>The glyphs on a page, in reading order. Safe to call from any thread.</summary>
    /// <param name="pageNumber">1-based page number.</param>
    /// <returns>The page's glyphs; empty for a page with no text (a scan, for example).</returns>
    public IReadOnlyList<TextGlyph> GetGlyphs(int pageNumber)
    {
        lock (gate)
        {
            if (cache.TryGetValue(pageNumber, out IReadOnlyList<TextGlyph>? cached))
            {
                return cached;
            }

            IReadOnlyList<TextGlyph> glyphs = Extract(document.GetPage(pageNumber));
            cache[pageNumber] = glyphs;
            return glyphs;
        }
    }

    /// <summary>
    /// The filled rectangles drawn on a page (card backgrounds, column panels), in reading coordinates. Text
    /// selection uses them as containers: the empty space inside a drawn card belongs to that card's text. Safe
    /// to call from any thread.
    /// </summary>
    /// <param name="pageNumber">1-based page number.</param>
    /// <returns>Rectangles in rendered page coordinates; empty for a page without drawn panels.</returns>
    public IReadOnlyList<Rect> GetContainers(int pageNumber)
    {
        lock (gate)
        {
            if (containers.TryGetValue(pageNumber, out IReadOnlyList<Rect>? cached))
            {
                return cached;
            }

            IReadOnlyList<Rect> rects = ExtractContainers(document.GetPage(pageNumber));
            containers[pageNumber] = rects;
            return rects;
        }
    }

    /// <summary>Finds the filled rectangular paths on a page that could hold a block of text.</summary>
    /// <param name="page">A PdfPig page.</param>
    /// <returns>Rectangles in rendered page coordinates, largest first.</returns>
    public static IReadOnlyList<Rect> ExtractContainers(Page page)
    {
        PageGeometry geometry = GeometryOf(page);
        double       pageArea = geometry.RenderedSize.Width * geometry.RenderedSize.Height;
        var          rects    = new List<Rect>();

        foreach (PdfPath path in page.Paths)
        {
            if (!path.IsFilled || path.IsClipping || (path.GetBoundingRectangle() is not { } box))
            {
                continue;
            }

            Rect rect = geometry.ToRendered(box.Left, box.Bottom, box.Right, box.Top);

            // Panels only: not hairlines or bullets, not the page background itself.
            if ((rect.Width < 24) || (rect.Height < 24) || ((rect.Width * rect.Height) > (0.9 * pageArea)))
            {
                continue;
            }

            // A card's background is often painted several times (shadow, fill, border); keep one of each.
            bool duplicate = rects.Exists(existing => (Math.Abs(existing.Left - rect.Left) < 1)
                && (Math.Abs(existing.Top - rect.Top) < 1) && (Math.Abs(existing.Width - rect.Width) < 1)
                && (Math.Abs(existing.Height - rect.Height) < 1));
            if (!duplicate)
            {
                rects.Add(rect);
            }
        }

        rects.Sort((a, b) => (b.Width * b.Height).CompareTo(a.Width * a.Height));
        return rects;
    }

    /// <summary>The rendered size of a page in points, rotation applied.</summary>
    /// <param name="pageNumber">1-based page number.</param>
    /// <returns>Width and height in points.</returns>
    public Size GetPageSize(int pageNumber)
    {
        lock (gate)
        {
            return GeometryOf(document.GetPage(pageNumber)).RenderedSize;
        }
    }

    /// <summary>
    /// Builds the glyph list for one parsed page in the order the file draws its text. That is the order every
    /// browser's PDF viewer selects in, and in a laid-out document it keeps each box or column together: a drag
    /// inside one card selects that card, not the neighbor's rows in between. Words are cut at spaces and at
    /// jumps in position; lines are cut where the next word is on another row or far away.
    /// </summary>
    /// <param name="page">A PdfPig page.</param>
    /// <returns>Glyphs in rendered page coordinates.</returns>
    public static IReadOnlyList<TextGlyph> Extract(Page page)
    {
        // How far apart (in letter heights) two letters can be and still share a word, and (in line heights) two
        // words on the same row can be and still share a line.
        const double MaxLetterGapInHeights = 0.35;
        const double MaxWordGapInLineHeights = 2.5;

        PageGeometry geometry = GeometryOf(page);

        // Letters as drawn, mapped into rendered space. Whitespace letters only mark word boundaries.
        var  words   = new List<MappedWord>();
        var  current = new List<(string Text, Rect Bounds)>();
        Rect wordBounds = Rect.Empty;
        foreach (Letter letter in page.Letters)
        {
            if (string.IsNullOrWhiteSpace(letter.Value))
            {
                FlushWord(words, current);
                continue;
            }

            PdfRectangle box   = letter.BoundingBox;
            Rect         glyph = geometry.ToRendered(box.Left, box.Bottom, box.Right, box.Top);
            if ((glyph.Width <= 0) || (glyph.Height <= 0))
            {
                continue;
            }

            // A jump sideways or off the row starts a new word even without a space in the file. The checks use
            // the word built so far, not just the last letter: a comma or apostrophe is a short glyph that would
            // otherwise fail to overlap its neighbor and split the word.
            if (current.Count > 0)
            {
                double gap  = glyph.Left - wordBounds.Right;
                bool   back = gap < -wordBounds.Height;
                if (!SameRow(wordBounds, glyph) || (gap > (MaxLetterGapInHeights * wordBounds.Height)) || back)
                {
                    FlushWord(words, current);
                }
            }

            current.Add((letter.Value, glyph));
            wordBounds = (current.Count == 1) ? glyph : Union(wordBounds, glyph);
        }

        FlushWord(words, current);

        // Lines: consecutive words stay together while they share a row and sit close; a word that starts a new
        // row, or is far from the last one, starts a new line.
        var lines = new List<TextLine>();
        foreach (MappedWord word in words)
        {
            TextLine? line = (lines.Count > 0) ? lines[^1] : null;
            if (line is not null)
            {
                double gap   = word.Bounds.Left - line.Right;
                bool   close = (gap >= -(line.Bottom - line.Top)) && (gap <= (MaxWordGapInLineHeights * (line.Bottom - line.Top)));
                if (SameRow(new Rect(line.Left, line.Top, line.Right - line.Left, line.Bottom - line.Top), word.Bounds) && close)
                {
                    line.Add(word);
                    continue;
                }
            }

            lines.Add(new TextLine(word));
        }

        var glyphs    = new List<TextGlyph>();
        int wordIndex = 0;
        for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            foreach (MappedWord word in lines[lineIndex].Words)
            {
                foreach ((string text, Rect bounds) in word.Letters)
                {
                    // Glyph boxes are tight around the ink; use the word's height so a line highlights evenly.
                    var box = new Rect(bounds.Left, word.Bounds.Top, bounds.Width, word.Bounds.Height);
                    glyphs.Add(new TextGlyph(text, box, wordIndex, lineIndex));
                }

                wordIndex++;
            }
        }

        return glyphs;
    }

    /// <summary>
    /// True when two boxes sit on the same row of text. A box at least 60% the height of the other must overlap
    /// it by half its height; a shorter box (a comma, an apostrophe) only has to touch the row, since it hangs
    /// below the baseline or floats above the x-height.
    /// </summary>
    private static bool SameRow(Rect row, Rect glyph)
    {
        double overlap = Math.Min(row.Bottom, glyph.Bottom) - Math.Max(row.Top, glyph.Top);
        double shorter = Math.Min(row.Height, glyph.Height);
        return (shorter < (0.6 * Math.Max(row.Height, glyph.Height))) ? overlap > 0 : overlap >= (0.5 * shorter);
    }

    private static Rect Union(Rect a, Rect b)
    {
        double left   = Math.Min(a.Left, b.Left);
        double top    = Math.Min(a.Top, b.Top);
        double right  = Math.Max(a.Right, b.Right);
        double bottom = Math.Max(a.Bottom, b.Bottom);
        return new Rect(left, top, right - left, bottom - top);
    }

    private static void FlushWord(List<MappedWord> words, List<(string Text, Rect Bounds)> letters)
    {
        if (letters.Count == 0)
        {
            return;
        }

        double left   = letters.Min(l => l.Bounds.Left);
        double top    = letters.Min(l => l.Bounds.Top);
        double right  = letters.Max(l => l.Bounds.Right);
        double bottom = letters.Max(l => l.Bounds.Bottom);
        words.Add(new MappedWord(new Rect(left, top, right - left, bottom - top), [.. letters]));
        letters.Clear();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (gate)
        {
            document.Dispose();
        }
    }

    private static PageGeometry GeometryOf(Page page)
    {
        // PdfPig already reports letters, paths and Width/Height inside the crop box, rotation applied, so the
        // page starts at the origin whatever its /CropBox and /Rotate say. Subtracting the crop origin again put
        // every glyph on a trimmed page (a crop box not at 0,0) out by that offset, and Windows renders the crop
        // box too, so the highlights missed the text they belonged to.
        return new PageGeometry(0, 0, page.Width, page.Height);
    }

    /// <summary>A word in rendered space with its visible letters, sorted left to right.</summary>
    private sealed record MappedWord(Rect Bounds, List<(string Text, Rect Bounds)> Letters);

    /// <summary>Words that share a baseline, with the vertical band they cover.</summary>
    private sealed class TextLine(MappedWord first)
    {
        public List<MappedWord> Words  { get; } = [first];
        public double           Top    { get; private set; } = first.Bounds.Top;
        public double           Bottom { get; private set; } = first.Bounds.Bottom;
        public double           Left   { get; private set; } = first.Bounds.Left;
        public double           Right  { get; private set; } = first.Bounds.Right;

        public void Add(MappedWord word)
        {
            Words.Add(word);
            Top    = Math.Min(Top, word.Bounds.Top);
            Bottom = Math.Max(Bottom, word.Bounds.Bottom);
            Left   = Math.Min(Left, word.Bounds.Left);
            Right  = Math.Max(Right, word.Bounds.Right);
        }
    }
}
