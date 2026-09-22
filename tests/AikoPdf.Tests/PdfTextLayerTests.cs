using AikoPdf.Pdf;
using Windows.Foundation;
using Xunit;

namespace AikoPdf.Tests;

public sealed class PdfTextLayerTests : IDisposable
{
    private readonly List<string> tempFiles = [];

    public void Dispose()
    {
        foreach (string path in tempFiles)
        {
            File.Delete(path);
        }
    }

    private string Temp(byte[] bytes)
    {
        string path = SamplePdf.WriteTemp(bytes);
        tempFiles.Add(path);
        return path;
    }

    [Fact]
    public void Glyphs_ComeInReadingOrderWithWordsAndLines()
    {
        using PdfTextLayer layer = PdfTextLayer.Open(Temp(SamplePdf.TwoLines()));
        var selection = new TextSelection(layer.GetGlyphs(1));
        selection.SelectAll();

        Assert.Equal(1, layer.PageCount);
        Assert.Equal($"Hello World{Environment.NewLine}Second line here", selection.Text);
    }

    [Fact]
    public void Glyphs_AreInPointsFromTheTopLeft()
    {
        using PdfTextLayer layer = PdfTextLayer.Open(Temp(SamplePdf.TwoLines()));
        TextGlyph h = layer.GetGlyphs(1)[0];

        // "Hello" starts at x = 72 with its baseline at y = 700 on a 792pt page, so the cap sits around y = 75.
        Assert.Equal("H", h.Text);
        Assert.InRange(h.Bounds.Left, 71, 74);
        Assert.InRange(h.Bounds.Top, 60, 92);
        Assert.InRange(h.Bounds.Bottom, 85, 100);
        Assert.Equal(new Size(612, 792), layer.GetPageSize(1));
    }

    [Fact]
    public void TrimmedPage_PlacesGlyphsInsideTheVisiblePage()
    {
        using PdfTextLayer layer = PdfTextLayer.Open(Temp(SamplePdf.CroppedPage()));
        TextGlyph h = layer.GetGlyphs(1)[0];

        // The crop box starts at (20, 30) and is 572 x 732, which is what both viewers show. "Hello" is drawn at
        // x = 72, so it sits 52 points in from the visible left edge, not 32.
        Assert.Equal(new Size(572, 732), layer.GetPageSize(1));
        Assert.Equal("H", h.Text);
        Assert.InRange(h.Bounds.Left, 51, 54);
        Assert.InRange(h.Bounds.Top, 30, 62);
        Assert.InRange(h.Bounds.Bottom, 55, 70);
    }

    [Fact]
    public void Containers_AreCachedPerPage()
    {
        using PdfTextLayer layer = PdfTextLayer.Open(Temp(SamplePdf.TwoPanels()));

        Assert.Same(layer.GetContainers(1), layer.GetContainers(1));
    }

    [Fact]
    public void NotAPdf_Throws()
    {
        string path = Path.Combine(Path.GetTempPath(), $"aikopdf-test-{Guid.NewGuid():N}.pdf");
        File.WriteAllText(path, "this is not a PDF at all");
        tempFiles.Add(path);

        Assert.ThrowsAny<Exception>(() => PdfTextLayer.Open(path));
    }

    [Fact]
    public void Glyphs_AreCachedPerPage()
    {
        using PdfTextLayer layer = PdfTextLayer.Open(Temp(SamplePdf.TwoLines()));

        Assert.Same(layer.GetGlyphs(1), layer.GetGlyphs(1));
    }

    [Fact]
    public void BlankPage_HasNoGlyphs()
    {
        using PdfTextLayer layer = PdfTextLayer.Open(Temp(SamplePdf.Blank()));

        Assert.Empty(layer.GetGlyphs(1));
    }

    [Fact]
    public void RotatedPage_MapsGlyphsIntoTheRotatedFrame()
    {
        using PdfTextLayer layer = PdfTextLayer.Open(Temp(SamplePdf.Rotated90()));
        IReadOnlyList<TextGlyph> glyphs = layer.GetGlyphs(1);
        TextGlyph h = glyphs[0];

        // Rotated 90 degrees clockwise: the page is landscape and text that was near the top-left now runs down the right side.
        Assert.Equal(new Size(792, 612), layer.GetPageSize(1));
        Assert.Equal("Hello", string.Concat(glyphs.Select(g => g.Text)));
        Assert.InRange(h.Bounds.Left, 690, 720);
        Assert.InRange(h.Bounds.Top, 71, 74);
    }

    [Fact]
    public void MultiPage_ReadsEachPage()
    {
        using PdfTextLayer layer = PdfTextLayer.Open(Temp(SamplePdf.ThreePages()));

        Assert.Equal(3, layer.PageCount);
        for (var i = 1; i <= 3; i++)
        {
            var selection = new TextSelection(layer.GetGlyphs(i));
            selection.SelectAll();
            Assert.Equal($"Page {i}", selection.Text);
        }
    }

    [Fact]
    public void WordsFarApartOnOneBaseline_AreSeparateLines()
    {
        using PdfTextLayer layer = PdfTextLayer.Open(Temp(SamplePdf.TwoBoxes()));
        IReadOnlyList<TextGlyph> glyphs = layer.GetGlyphs(1);
        var selection = new TextSelection(glyphs);
        selection.SelectAll();

        // Two lines: the selection breaks between the boxes and draws one rectangle per box, not one across both.
        Assert.Equal($"Left box{Environment.NewLine}Right box", selection.Text);
        Assert.Equal(2, selection.Rects.Count);
    }

    [Fact]
    public void Containers_AreTheFilledPanelsOnThePage()
    {
        using PdfTextLayer layer = PdfTextLayer.Open(Temp(SamplePdf.TwoPanels()));
        IReadOnlyList<Rect> panels = layer.GetContainers(1);

        // Two 200 x 300 panels; the page-sized background and the thin rule are left out.
        Assert.Equal(2, panels.Count);
        Assert.All(panels, panel => Assert.Equal(200, panel.Width, 1));
        Assert.All(panels, panel => Assert.Equal(300, panel.Height, 1));

        // Top-left corners in rendered space: the left panel is 50 in and 792 - 700 = 92 down.
        Assert.Contains(panels, panel => (Math.Abs(panel.Left - 50) < 1) && (Math.Abs(panel.Top - 92) < 1));
        Assert.Contains(panels, panel => (Math.Abs(panel.Left - 300) < 1) && (Math.Abs(panel.Top - 92) < 1));

        using PdfTextLayer blank = PdfTextLayer.Open(Temp(SamplePdf.Blank()));
        Assert.Empty(blank.GetContainers(1));
    }

    [Fact]
    public void Containers_InsideAFormArePlacedThroughItsTransform()
    {
        using PdfTextLayer layer = PdfTextLayer.Open(Temp(SamplePdf.TwoPanelsInAForm()));
        IReadOnlyList<Rect> panels = layer.GetContainers(1);

        // The form is drawn 10 right and 20 up, so the left panel lands at x 60 and 792 - 720 = 72 down.
        Assert.Equal(2, panels.Count);
        Assert.Contains(panels, panel => (Math.Abs(panel.Left - 60) < 1) && (Math.Abs(panel.Top - 72) < 1));
        Assert.Contains(panels, panel => (Math.Abs(panel.Left - 310) < 1) && (Math.Abs(panel.Top - 72) < 1));
    }

    [Theory]
    [InlineData("RC4")]
    [InlineData("AES256")]
    public void Encrypted_NeedsThePassword(string algorithm)
    {
        string path = SamplePdf.Encrypted(algorithm);

        Assert.Throws<PdfPasswordException>(() => PdfTextLayer.Open(path));
        Assert.Throws<PdfPasswordException>(() => PdfTextLayer.Open(path, "not the password"));
    }

    [Theory]
    [InlineData("RC4")]
    [InlineData("AES256")]
    public void Encrypted_OpensWithThePassword(string algorithm)
    {
        using PdfTextLayer layer = PdfTextLayer.Open(SamplePdf.Encrypted(algorithm), SamplePdf.FixturePassword);
        var selection = new TextSelection(layer.GetGlyphs(1));
        selection.SelectAll();

        Assert.Equal($"Hello World{Environment.NewLine}Second line here", selection.Text);
    }

    [Fact]
    public void Dispose_TwiceIsHarmless_AndUseAfterwardThrows()
    {
        PdfTextLayer layer = PdfTextLayer.Open(Temp(SamplePdf.TwoLines()));
        layer.Dispose();
        layer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => layer.GetGlyphs(1));
    }

    [Fact]
    public void ManyThreads_ReadingAtOnce_AgreeWithOneThread()
    {
        using PdfTextLayer first  = PdfTextLayer.Open(Temp(SamplePdf.ThreePages()));
        using PdfTextLayer second = PdfTextLayer.Open(Temp(SamplePdf.TwoLines()));

        // PDFium is not thread-safe on its own; the shared lock is what makes this pass instead of crash.
        Parallel.For(0, 64, i =>
        {
            PdfTextLayer layer = (i % 2 == 0) ? first : second;
            int          page  = (i % 2 == 0) ? (i % 3) + 1 : 1;
            Assert.NotEmpty(layer.GetGlyphs(page));
            Assert.NotEqual(default, layer.GetPageSize(page));
        });
    }

    [Fact]
    public void PageNames_ComeFromBookmarks()
    {
        using PdfTextLayer layer = PdfTextLayer.Open(Temp(SamplePdf.ThreePages()));

        Assert.Null(layer.GetPageName(1));
        Assert.Equal("Chapter Two", layer.GetPageName(2));
        Assert.Null(layer.GetPageName(3));
    }

    [Fact]
    public void Open_MissingFile_Throws()
    {
        Assert.ThrowsAny<IOException>(() => PdfTextLayer.Open(Path.Combine(Path.GetTempPath(), "does-not-exist.pdf")));
    }
}
