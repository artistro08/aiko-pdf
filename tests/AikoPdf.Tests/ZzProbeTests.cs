using System.Text;
using AikoPdf.Pdf;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using Windows.Foundation;
using Xunit;
using Xunit.Abstractions;

namespace AikoPdf.Tests;

public sealed class ZzProbeTests(ITestOutputHelper output)
{
    private static byte[] Build(string pageDict)
    {
        const string content = "BT /F1 24 Tf 72 700 Td (Hello) Tj ET";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            pageDict,
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
        ];

        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
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

    private static string Temp(byte[] bytes) => SamplePdf.WriteTemp(bytes);

    private void Dump(string label, string pageDict)
    {
        try
        {
            using PdfDocument doc = PdfDocument.Open(Temp(Build(pageDict)), new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
            Page page = doc.GetPage(1);
            PdfRectangle crop = page.CropBox.Bounds;
            PdfRectangle media = page.MediaBox.Bounds;
            output.WriteLine($"{label}: rot={page.Rotation.Value} W={page.Width} H={page.Height} media=({media.Left},{media.Bottom},{media.Right},{media.Top}) crop=({crop.Left},{crop.Bottom},{crop.Right},{crop.Top})");
            var letters = page.Letters;
            if (letters.Count > 0)
            {
                PdfRectangle b = letters[0].BoundingBox;
                output.WriteLine($"    letter0 '{letters[0].Value}' box=({b.Left:F1},{b.Bottom:F1},{b.Right:F1},{b.Top:F1})");
            }

            var glyphs = PdfTextLayer.Extract(page);
            output.WriteLine($"    glyphs={glyphs.Count} first={(glyphs.Count > 0 ? glyphs[0].Bounds.ToString() : "-")}");
        }
        #pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            output.WriteLine($"{label}: THREW {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static double ZeroNaN() => double.NaN;

    private static double InfVal() => double.PositiveInfinity;

    [Fact]
    public void Probe()
    {
        Dump("plain", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>");
        Dump("crop-offset", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /CropBox [20 30 592 762] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>");
        Dump("crop-offset-rot90", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /CropBox [20 30 592 762] /Rotate 90 /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>");
        Dump("rot-negative90", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Rotate -90 /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>");
        Dump("rot360", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Rotate 360 /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>");
        Dump("crop-inverted", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /CropBox [0 792 612 0] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>");
        Dump("crop-bigger-than-media", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /CropBox [-100 -100 800 900] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>");

        // Out-of-range page access.
        using PdfDocument doc = PdfDocument.Open(Temp(SamplePdf.TwoLines()), new ParsingOptions { UseLenientParsing = true });
        output.WriteLine($"pages={doc.NumberOfPages}");
        foreach (int n in new[] { 0, 2, -1 })
        {
            try
            {
                doc.GetPage(n);
                output.WriteLine($"GetPage({n}) OK");
            }
            #pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
            {
                output.WriteLine($"GetPage({n}) THREW {ex.GetType().Name}");
            }
        }

        // Disposed document access.
        PdfDocument disposed = PdfDocument.Open(Temp(SamplePdf.TwoLines()), new ParsingOptions { UseLenientParsing = true });
        disposed.Dispose();
        try
        {
            disposed.GetPage(1);
            output.WriteLine("disposed GetPage OK");
        }
        #pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            output.WriteLine($"disposed GetPage THREW {ex.GetType().Name}");
        }

        // Negative Size / Rect behavior.
        try
        {
            var s = new Size(-5, -10);
            output.WriteLine($"negative Size ok: {s}");
        }
        #pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            output.WriteLine($"negative Size THREW {ex.GetType().Name}");
        }

        try
        {
            var r = new Rect(0, 0, double.NaN, double.NaN);
            output.WriteLine($"NaN Rect ok: {r} contains(1,1)={r.Contains(new Point(1, 1))}");
        }
        #pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            output.WriteLine($"NaN Rect THREW {ex.GetType().Name}");
        }

        output.WriteLine($"Math.Max(1,NaN)={Math.Max(1, double.NaN)} (uint)NaN={(uint)(double)ZeroNaN()} (uint)Inf={(uint)(double)InfVal()}");
        output.WriteLine($"1e308*1000={1e308 * 1000} lessThanMax={(1e308 * 1000) < double.MaxValue}");
    }
}
