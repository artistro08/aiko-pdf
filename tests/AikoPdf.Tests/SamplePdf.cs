using System.Text;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Outline;
using UglyToad.PdfPig.Outline.Destinations;
using UglyToad.PdfPig.Writer;

namespace AikoPdf.Tests;

/// <summary>
/// Small PDFs generated on the fly so the tests never depend on files checked into the repo.
/// </summary>
internal static class SamplePdf
{
    /// <summary>A US Letter page with "Hello World" on one line and "Second line here" below it, both at 24pt.</summary>
    public static byte[] TwoLines()
    {
        var builder = new PdfDocumentBuilder();
        PdfDocumentBuilder.AddedFont font = builder.AddStandard14Font(Standard14Font.Helvetica);
        PdfPageBuilder page = builder.AddPage(PageSize.Letter);
        page.AddText("Hello World", 24, new PdfPoint(72, 700), font);
        page.AddText("Second line here", 24, new PdfPoint(72, 660), font);
        return builder.Build();
    }

    /// <summary>A US Letter page with "Left box" and, far to the right on the same baseline, "Right box".</summary>
    public static byte[] TwoBoxes()
    {
        var builder = new PdfDocumentBuilder();
        PdfDocumentBuilder.AddedFont font = builder.AddStandard14Font(Standard14Font.Helvetica);
        PdfPageBuilder page = builder.AddPage(PageSize.Letter);
        page.AddText("Left box", 12, new PdfPoint(72, 700), font);
        page.AddText("Right box", 12, new PdfPoint(400, 700), font);
        return builder.Build();
    }

    /// <summary>
    /// A US Letter page with a full-page background, two filled 200 x 300 panels side by side, a thin filled
    /// rule, and a word inside each panel.
    /// </summary>
    public static byte[] TwoPanels()
    {
        var builder = new PdfDocumentBuilder();
        PdfDocumentBuilder.AddedFont font = builder.AddStandard14Font(Standard14Font.Helvetica);
        PdfPageBuilder page = builder.AddPage(PageSize.Letter);
        page.SetStrokeColor(0, 0, 0);
        page.SetTextAndFillColor(30, 30, 60);
        page.DrawRectangle(new PdfPoint(0, 0), 612, 792, 0, fill: true);
        page.SetTextAndFillColor(60, 60, 120);
        page.DrawRectangle(new PdfPoint(50, 400), 200, 300, 0, fill: true);
        page.DrawRectangle(new PdfPoint(300, 400), 200, 300, 0, fill: true);
        page.DrawRectangle(new PdfPoint(50, 380), 450, 2, 0, fill: true);
        page.SetTextAndFillColor(255, 255, 255);
        page.AddText("Left", 12, new PdfPoint(70, 650), font);
        page.AddText("Right", 12, new PdfPoint(320, 650), font);
        return builder.Build();
    }

    /// <summary>A US Letter page with no content at all.</summary>
    public static byte[] Blank()
    {
        var builder = new PdfDocumentBuilder();
        builder.AddPage(PageSize.Letter);
        return builder.Build();
    }

    /// <summary>Three US Letter pages, each with its number as text, and a bookmark called "Chapter Two" on page 2.</summary>
    public static byte[] ThreePages()
    {
        var builder = new PdfDocumentBuilder();
        PdfDocumentBuilder.AddedFont font = builder.AddStandard14Font(Standard14Font.Helvetica);
        for (var i = 1; i <= 3; i++)
        {
            builder.AddPage(PageSize.Letter).AddText($"Page {i}", 24, new PdfPoint(72, 700), font);
        }

        var destination = new ExplicitDestination(2, ExplicitDestinationType.FitPage, ExplicitDestinationCoordinates.Empty);
        builder.Bookmarks = new Bookmarks([new DocumentBookmarkNode("Chapter Two", 0, destination, [])]);
        return builder.Build();
    }

    /// <summary>
    /// A US Letter page rotated 90 degrees with "Hello" at (72, 700). Written by hand because PdfPig's builder has
    /// no rotation setter; the xref table is computed so both parsers accept it without repair.
    /// </summary>
    public static byte[] Rotated90()
    {
        const string content = "BT /F1 24 Tf 72 700 Td (Hello) Tj ET";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Rotate 90 /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
        ];

        return Assemble(objects);
    }

    /// <summary>
    /// A trimmed page: media box 612 x 792 with a crop box inset 20 points from the left and 30 from the bottom,
    /// and "Hello" drawn at (72, 700) in media-box coordinates. Both viewers show only the crop box, so the text
    /// sits about 52 points in from the visible left edge.
    /// </summary>
    public static byte[] CroppedPage()
    {
        const string content = "BT /F1 24 Tf 72 700 Td (Hello) Tj ET";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /CropBox [20 30 592 762] "
                + "/Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
        ];

        return Assemble(objects);
    }

    /// <summary>Builds a one-page PDF by hand, computing the xref table so both parsers accept it without repair.</summary>
    /// <param name="objects">The body objects, in order, starting at object 1.</param>
    /// <returns>The file's bytes.</returns>
    private static byte[] Assemble(string[] objects)
    {
        var pdf     = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets)
        {
            pdf.Append($"{offset:D10} 00000 n \n");
        }

        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    /// <summary>Writes bytes to a fresh temp file with a .pdf extension and returns its path.</summary>
    public static string WriteTemp(byte[] bytes)
    {
        string path = Path.Combine(Path.GetTempPath(), $"aikopdf-test-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
