using System.Runtime.InteropServices;
using Windows.Foundation;

namespace AikoPdf.Pdf;

/// <summary>
/// The text behind a rendered page: every visible character with its position, in reading order, so the viewer
/// can select and copy text from what is otherwise a bitmap.
///
/// Windows renders the pages but exposes no text, so this opens the same file with PDFium, the engine inside
/// Chrome, which reads each character's box, the outline that names pages, and the rectangles drawn behind cards
/// and columns. Glyph lists are built on first use per page and cached for the life of the document. PDFium is not
/// safe to call from two threads at once, so every call goes through one lock shared by all documents.
/// </summary>
/// <remarks>
/// @author Devin Green (Artistro08)
/// @link https://pdfium.googlesource.com/pdfium/+/refs/heads/main/public/fpdf_text.h
/// </remarks>
public sealed class PdfTextLayer : IDisposable
{
    // Page-to-screen mapping in PDFium hands back whole numbers, so pages are mapped at this many units per point
    // and divided back down: positions stay accurate to a hundredth of a point.
    private const int Precision = 100;

    // Most outline entries read from one file, however it is nested or looped.
    private const int MaxOutlineEntries = 10_000;

    private readonly nint                                      document;
    private readonly Dictionary<int, IReadOnlyList<TextGlyph>> cache      = [];
    private readonly Dictionary<int, IReadOnlyList<Rect>>      containers = [];
    private readonly Dictionary<int, string>                   pageNames  = [];

    // The file's bytes, held in unmanaged memory for as long as the document is open: PDFium reads from them as
    // it needs them instead of taking its own copy.
    private nint data;

    private PdfTextLayer(nint document, nint data)
    {
        this.document = document;
        this.data     = data;
        PageCount     = Pdfium.FPDF_GetPageCount(document);
    }

    /// <summary>Number of pages in the document.</summary>
    public int PageCount { get; }

    /// <summary>Opens a PDF for text extraction.</summary>
    /// <param name="path">Full path of the file.</param>
    /// <param name="password">The user password for an encrypted file, or null.</param>
    /// <returns>The open text layer.</returns>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="PdfPasswordException">The file needs a password, or the one given is wrong.</exception>
    /// <exception cref="InvalidDataException">The file isn't a PDF PDFium can read.</exception>
    public static PdfTextLayer Open(string path, string? password = null)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The PDF file was not found.", path);
        }

        // Shared for reading and writing, so a file another app still holds open (a sync client, the tool that
        // just wrote it) opens anyway.
        byte[] bytes;
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            bytes = new byte[file.Length];
            file.ReadExactly(bytes);
        }

        nint data = Marshal.AllocHGlobal(Math.Max(1, bytes.Length));
        Marshal.Copy(bytes, 0, data, bytes.Length);

        lock (Pdfium.Gate)
        {
            nint document = Pdfium.FPDF_LoadMemDocument64(data, (nuint)bytes.Length, password);
            if (document != nint.Zero)
            {
                // The constructor only stores the handles, so once it returns the layer owns them and its Dispose
                // (or finalizer) frees them exactly once, whatever fails after.
                var layer = new PdfTextLayer(document, data);
                try
                {
                    int budget = MaxOutlineEntries;
                    layer.ReadOutline(Pdfium.FPDFBookmark_GetFirstChild(document, nint.Zero), depth: 0, ref budget);
                    return layer;
                }
                catch
                {
                    layer.Dispose();
                    throw;
                }
            }

            uint error = Pdfium.FPDF_GetLastError();
            Marshal.FreeHGlobal(data);
            throw error switch
            {
                Pdfium.ErrorPassword => new PdfPasswordException(),
                Pdfium.ErrorFile     => new IOException($"PDFium could not read {path}."),
                Pdfium.ErrorSecurity => new InvalidDataException("The PDF uses a security handler PDFium doesn't support."),
                _                    => new InvalidDataException($"PDFium could not open the file (error {error})."),
            };
        }
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
        lock (Pdfium.Gate)
        {
            if (cache.TryGetValue(pageNumber, out IReadOnlyList<TextGlyph>? cached))
            {
                return cached;
            }

            IReadOnlyList<TextGlyph> glyphs = WithPage(pageNumber, page => BuildGlyphs(ReadLetters(page)));
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
    /// <returns>Rectangles in rendered page coordinates, largest first; empty for a page without drawn panels.</returns>
    public IReadOnlyList<Rect> GetContainers(int pageNumber)
    {
        lock (Pdfium.Gate)
        {
            if (containers.TryGetValue(pageNumber, out IReadOnlyList<Rect>? cached))
            {
                return cached;
            }

            IReadOnlyList<Rect> rects = WithPage(pageNumber, page =>
            {
                var found = new List<Rect>();
                int count = Pdfium.FPDFPage_CountObjects(page);
                for (int i = 0; i < count; i++)
                {
                    CollectFilledPaths(page, Pdfium.FPDFPage_GetObject(page, i), Pdfium.Matrix.Identity, found, depth: 0);
                }

                return FilterContainers(found, SizeOf(page));
            });

            containers[pageNumber] = rects;
            return rects;
        }
    }

    /// <summary>The rendered size of a page in points, rotation applied.</summary>
    /// <param name="pageNumber">1-based page number.</param>
    /// <returns>Width and height in points.</returns>
    public Size GetPageSize(int pageNumber)
    {
        lock (Pdfium.Gate)
        {
            return WithPage(pageNumber, SizeOf);
        }
    }

    /// <summary>Closes the document in PDFium and frees the file's bytes, if a caller never disposed it.</summary>
    ~PdfTextLayer()
    {
        Release();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Release();
        GC.SuppressFinalize(this);
    }

    /// <summary>Closes the document and frees its bytes, once; later calls do nothing.</summary>
    private void Release()
    {
        lock (Pdfium.Gate)
        {
            if (data == nint.Zero)
            {
                return;
            }

            Pdfium.FPDF_CloseDocument(document);
            Marshal.FreeHGlobal(data);
            data = nint.Zero;
        }
    }

    // =========================================================================
    // GLYPH LAYOUT
    // =========================================================================

    /// <summary>
    /// Builds the glyph list for a page from its letters in the order the file draws them. That is the order every
    /// browser's PDF viewer selects in, and in a laid-out document it keeps each box or column together: a drag
    /// inside one card selects that card, not the neighbor's rows in between. Words are cut at spaces and at
    /// jumps in position; lines are cut where the next word is on another row or far away.
    /// </summary>
    /// <param name="letters">
    /// The page's characters in drawing order, each with its box in rendered page coordinates. Whitespace entries
    /// only mark word boundaries; their boxes are ignored.
    /// </param>
    /// <returns>Glyphs in rendered page coordinates, numbered by word and line.</returns>
    public static IReadOnlyList<TextGlyph> BuildGlyphs(IReadOnlyList<(string Text, Rect Bounds)> letters)
    {
        // How far apart (in letter heights) two letters can be and still share a word, and (in line heights) two
        // words on the same row can be and still share a line.
        const double MaxLetterGapInHeights   = 0.35;
        const double MaxWordGapInLineHeights = 2.5;

        var  words      = new List<MappedWord>();
        var  current    = new List<(string Text, Rect Bounds)>();
        Rect wordBounds = Rect.Empty;
        foreach ((string text, Rect glyph) in letters)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                FlushWord(words, current);
                continue;
            }

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

            current.Add((text, glyph));
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
    /// Keeps the rectangles that could hold a block of text: not hairlines or bullets, not the page background, and
    /// only one of each where a card is painted several times over (shadow, fill, border).
    /// </summary>
    /// <param name="rects">Filled shapes' bounds in rendered page coordinates.</param>
    /// <param name="page">The page's rendered size in points.</param>
    /// <returns>The panels, largest first.</returns>
    public static IReadOnlyList<Rect> FilterContainers(IEnumerable<Rect> rects, Size page)
    {
        double pageArea = page.Width * page.Height;
        var    kept     = new List<Rect>();
        foreach (Rect rect in rects)
        {
            if ((rect.Width < 24) || (rect.Height < 24) || ((rect.Width * rect.Height) > (0.9 * pageArea)))
            {
                continue;
            }

            bool duplicate = kept.Exists(existing => (Math.Abs(existing.Left - rect.Left) < 1)
                && (Math.Abs(existing.Top - rect.Top) < 1) && (Math.Abs(existing.Width - rect.Width) < 1)
                && (Math.Abs(existing.Height - rect.Height) < 1));
            if (!duplicate)
            {
                kept.Add(rect);
            }
        }

        kept.Sort((a, b) => (b.Width * b.Height).CompareTo(a.Width * a.Height));
        return kept;
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

    // =========================================================================
    // READING FROM PDFIUM (CALLERS HOLD Pdfium.Gate)
    // =========================================================================

    /// <summary>Loads a page, runs something against it, and closes it again.</summary>
    private T WithPage<T>(int pageNumber, Func<nint, T> read)
    {
        ObjectDisposedException.ThrowIf(data == nint.Zero, this);

        nint page = Pdfium.FPDF_LoadPage(document, pageNumber - 1);
        if (page == nint.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pageNumber), pageNumber, "The page could not be loaded.");
        }

        try
        {
            return read(page);
        }
        finally
        {
            Pdfium.FPDF_ClosePage(page);
        }
    }

    private static Size SizeOf(nint page)
        => new(Pdfium.FPDF_GetPageWidthF(page), Pdfium.FPDF_GetPageHeightF(page));

    /// <summary>
    /// Maps a box from PDF page space (points, origin bottom-left, before the page's rotation) to rendered page
    /// coordinates (points, origin top-left, crop box and rotation applied), the space Windows draws the page in.
    /// </summary>
    private static Rect ToRendered(nint page, Size size, double left, double bottom, double right, double top)
    {
        int width  = (int)Math.Round(size.Width * Precision);
        int height = (int)Math.Round(size.Height * Precision);
        Pdfium.FPDF_PageToDevice(page, 0, 0, width, height, 0, left, bottom, out int x1, out int y1);
        Pdfium.FPDF_PageToDevice(page, 0, 0, width, height, 0, right, top, out int x2, out int y2);

        double x = Math.Min(x1, x2) / (double)Precision;
        double y = Math.Min(y1, y2) / (double)Precision;
        return new Rect(x, y, Math.Abs(x2 - x1) / (double)Precision, Math.Abs(y2 - y1) / (double)Precision);
    }

    /// <summary>
    /// Reads a page's characters in drawing order with their boxes. Spaces and line breaks PDFium adds between
    /// runs come through as whitespace, which only marks a word boundary; a character outside the Basic
    /// Multilingual Plane arrives as two halves and is joined back into one glyph.
    /// </summary>
    private static List<(string Text, Rect Bounds)> ReadLetters(nint page)
    {
        var  letters  = new List<(string Text, Rect Bounds)>();
        nint textPage = Pdfium.FPDFText_LoadPage(page);
        if (textPage == nint.Zero)
        {
            return letters;
        }

        try
        {
            Size size  = SizeOf(page);
            int  count = Pdfium.FPDFText_CountChars(textPage);
            for (int i = 0; i < count; i++)
            {
                uint code = Pdfium.FPDFText_GetUnicode(textPage, i);
                if ((code < 0x20) || (code == 0xFFFE) || (code == 0xFFFF) || ((code <= 0xFFFF) && char.IsWhiteSpace((char)code)))
                {
                    letters.Add((" ", Rect.Empty));
                    continue;
                }

                if (!Pdfium.FPDFText_GetCharBox(textPage, i, out double left, out double right, out double bottom, out double top))
                {
                    continue;
                }

                string text;
                if (code > 0xFFFF)
                {
                    text = (code <= 0x10FFFF) ? char.ConvertFromUtf32((int)code) : "�";
                }
                else if (char.IsHighSurrogate((char)code) && (i + 1 < count)
                    && char.IsLowSurrogate((char)Pdfium.FPDFText_GetUnicode(textPage, i + 1)))
                {
                    text = new string([(char)code, (char)Pdfium.FPDFText_GetUnicode(textPage, i + 1)]);
                    i++;
                }
                else
                {
                    text = ((char)code).ToString();
                }

                letters.Add((text, ToRendered(page, size, left, bottom, right, top)));
            }
        }
        finally
        {
            Pdfium.FPDFText_ClosePage(textPage);
        }

        return letters;
    }

    /// <summary>
    /// Adds the bounds of every filled path on a page, looking inside form XObjects too, whose contents are
    /// placed on the page through their own transform.
    /// </summary>
    private static void CollectFilledPaths(nint page, nint pageObject, Pdfium.Matrix toPage, List<Rect> found, int depth)
    {
        // Forms can nest; a malformed file could nest them without end.
        const int MaxDepth = 16;
        if ((pageObject == nint.Zero) || (depth > MaxDepth))
        {
            return;
        }

        int type = Pdfium.FPDFPageObj_GetType(pageObject);
        if (type == Pdfium.ObjectForm)
        {
            Pdfium.Matrix inner = Pdfium.FPDFPageObj_GetMatrix(pageObject, out Pdfium.Matrix form)
                ? toPage.After(form)
                : toPage;
            int children = Pdfium.FPDFFormObj_CountObjects(pageObject);
            for (int i = 0; i < children; i++)
            {
                CollectFilledPaths(page, Pdfium.FPDFFormObj_GetObject(pageObject, (uint)i), inner, found, depth + 1);
            }

            return;
        }

        if ((type != Pdfium.ObjectPath)
            || !Pdfium.FPDFPath_GetDrawMode(pageObject, out int fill, out bool _)
            || (fill == Pdfium.FillNone)
            || !Pdfium.FPDFPageObj_GetBounds(pageObject, out float left, out float bottom, out float right, out float top))
        {
            return;
        }

        // Top-level bounds are already in page space; inside a form they are in the form's own space.
        (double x1, double y1) = toPage.Apply(left, bottom);
        (double x2, double y2) = toPage.Apply(right, top);
        found.Add(ToRendered(page, SizeOf(page), Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2)));
    }

    /// <summary>
    /// Walks the outline depth first. The first entry that points at a page names that page; later ones pointing
    /// at the same page don't replace it.
    /// </summary>
    /// <param name="bookmark">The first bookmark at this level.</param>
    /// <param name="depth">How deep this level is.</param>
    /// <param name="budget">Entries left to read across the whole outline; counts down as the walk goes.</param>
    private void ReadOutline(nint bookmark, int depth, ref int budget)
    {
        // Outlines can be deep and, when damaged, circular; the depth limit and the shared budget cap the walk.
        const int MaxDepth = 32;

        while ((bookmark != nint.Zero) && (depth <= MaxDepth) && (budget-- > 0))
        {
            string title = Pdfium.BookmarkTitle(bookmark).Trim();
            nint   dest  = Pdfium.FPDFBookmark_GetDest(document, bookmark);
            if (dest == nint.Zero)
            {
                nint action = Pdfium.FPDFBookmark_GetAction(bookmark);
                dest = (action == nint.Zero) ? nint.Zero : Pdfium.FPDFAction_GetDest(document, action);
            }

            int index = (dest == nint.Zero) ? -1 : Pdfium.FPDFDest_GetDestPageIndex(document, dest);
            if ((index >= 0) && (title.Length > 0))
            {
                pageNames.TryAdd(index + 1, title);
            }

            ReadOutline(Pdfium.FPDFBookmark_GetFirstChild(document, bookmark), depth + 1, ref budget);
            bookmark = Pdfium.FPDFBookmark_GetNextSibling(document, bookmark);
        }
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
