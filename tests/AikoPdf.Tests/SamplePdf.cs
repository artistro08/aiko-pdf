using System.Globalization;
using System.Text;

namespace AikoPdf.Tests;

/// <summary>
/// Small PDFs written by hand on the fly, so the tests never depend on a PDF library to make their inputs, and a
/// few encrypted ones checked in under Fixtures, which can't be written without one. Every page is US Letter
/// (612 x 792 points) and every text run is Helvetica.
/// </summary>
internal static class SamplePdf
{
    /// <summary>The password on every encrypted fixture.</summary>
    public const string FixturePassword = "letmein";

    /// <summary>A page with "Hello World" on one line and "Second line here" below it, both at 24pt.</summary>
    public static byte[] TwoLines() => Document(Text(72, 700, 24, "Hello World") + Text(72, 660, 24, "Second line here"));

    /// <summary>A page with "Left box" and, far to the right on the same baseline, "Right box".</summary>
    public static byte[] TwoBoxes() => Document(Text(72, 700, 12, "Left box") + Text(400, 700, 12, "Right box"));

    /// <summary>
    /// A page with a full-page background, two filled 200 x 300 panels side by side, a thin filled rule, and a
    /// word inside each panel.
    /// </summary>
    public static byte[] TwoPanels() => Document(
        Fill(0.12, 0.12, 0.24) + Rect(0, 0, 612, 792)
        + Fill(0.24, 0.24, 0.47) + Rect(50, 400, 200, 300) + Rect(300, 400, 200, 300) + Rect(50, 380, 450, 2)
        + Fill(1, 1, 1) + Text(70, 650, 12, "Left") + Text(320, 650, 12, "Right"));

    /// <summary>
    /// The same two panels as <see cref="TwoPanels"/>, but drawn inside a form XObject that the page places with a
    /// transform: shifted 10 points right and 20 up. Design tools often group a card like this.
    /// </summary>
    public static byte[] TwoPanelsInAForm()
    {
        string form    = Fill(0.24, 0.24, 0.47) + Rect(50, 400, 200, 300) + Rect(300, 400, 200, 300);
        string content = "q 1 0 0 1 10 20 cm /Card Do Q\n" + Text(70, 650, 12, "Left");
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << /Font << /F1 4 0 R >> /XObject << /Card 6 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            Stream(content),
            Stream(form, "/Type /XObject /Subtype /Form /BBox [0 0 612 792]"),
        ];

        return Assemble(objects);
    }

    /// <summary>A page with no content at all.</summary>
    public static byte[] Blank() => Document(string.Empty);

    /// <summary>Three pages, each with its number as text, and a bookmark called "Chapter Two" on page 2.</summary>
    public static byte[] ThreePages()
    {
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R /Outlines 10 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 5 0 R 7 0 R] /Count 3 >>",
            Page(4), Stream(Text(72, 700, 24, "Page 1")),
            Page(6), Stream(Text(72, 700, 24, "Page 2")),
            Page(8), Stream(Text(72, 700, 24, "Page 3")),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /Outlines /First 11 0 R /Last 11 0 R /Count 1 >>",
            "<< /Title (Chapter Two) /Parent 10 0 R /Dest [5 0 R /Fit] >>",
        ];

        return Assemble(objects);
    }

    /// <summary>A page rotated 90 degrees with "Hello" drawn at (72, 700).</summary>
    public static byte[] Rotated90() => Document(Text(72, 700, 24, "Hello"), pageExtra: "/Rotate 90");

    /// <summary>
    /// A trimmed page: a crop box inset 20 points from the left and 30 from the bottom, with "Hello" drawn at
    /// (72, 700) in media-box coordinates. Viewers show only the crop box, so the text sits about 52 points in from
    /// the visible left edge.
    /// </summary>
    public static byte[] CroppedPage() => Document(Text(72, 700, 24, "Hello"), pageExtra: "/CropBox [20 30 592 762]");

    /// <summary>
    /// The two-line page from <see cref="TwoLines"/>, encrypted with <see cref="FixturePassword"/>. Written by
    /// pypdf, because encryption needs a real PDF writer.
    /// </summary>
    /// <param name="algorithm"><c>RC4</c> for 128-bit RC4, the older kind; <c>AES256</c> for PDF 2.0's AES-256.</param>
    /// <returns>The path to the checked-in file.</returns>
    public static string Encrypted(string algorithm)
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", $"two-lines-{algorithm.ToLowerInvariant()}.pdf");

    /// <summary>Writes bytes to a fresh temp file with a .pdf extension and returns its path.</summary>
    public static string WriteTemp(byte[] bytes)
    {
        string path = Path.Combine(Path.GetTempPath(), $"aikopdf-test-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // =========================================================================
    // WRITING
    // =========================================================================

    private static string Text(double x, double y, double size, string text)
        => Invariant($"BT /F1 {size} Tf {x} {y} Td ({text.Replace("(", "\\(").Replace(")", "\\)")}) Tj ET\n");

    private static string Fill(double r, double g, double b) => Invariant($"{r} {g} {b} rg\n");

    private static string Rect(double x, double y, double w, double h) => Invariant($"{x} {y} {w} {h} re f\n");

    private static string Page(int contents, string extra = "")
        => $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] {extra} /Resources << /Font << /F1 9 0 R >> >> /Contents {contents} 0 R >>";

    private static string Stream(string content, string dictionary = "")
        => $"<< {dictionary} /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream";

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    /// <summary>A one-page document with the given content stream.</summary>
    private static byte[] Document(string content, string pageExtra = "")
    {
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] {pageExtra} /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            Stream(content),
        ];

        return Assemble(objects);
    }

    /// <summary>Lays out objects 1..n with a correct cross-reference table, so no reader has to repair the file.</summary>
    /// <param name="objects">The body objects, in order, starting at object 1.</param>
    /// <returns>The file's bytes.</returns>
    private static byte[] Assemble(string[] objects)
    {
        var pdf     = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = pdf.Length;
        pdf.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets)
        {
            pdf.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        pdf.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
