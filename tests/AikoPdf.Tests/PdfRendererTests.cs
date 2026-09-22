using AikoPdf.Pdf;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Xunit;

namespace AikoPdf.Tests;

public sealed class PdfRendererTests : IDisposable
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
    public async Task Open_ReadsPageCountAndSizes()
    {
        using PdfRenderer renderer = await PdfRenderer.OpenAsync(Temp(SamplePdf.ThreePages()));

        Assert.Equal(3, renderer.PageCount);
        Assert.All(renderer.PageSizes, size => Assert.Equal(new Size(612, 792), size));
    }

    [Fact]
    public async Task Open_RotatedPage_ReportsLandscapeSize()
    {
        using PdfRenderer renderer = await PdfRenderer.OpenAsync(Temp(SamplePdf.Rotated90()));

        Assert.Equal(new Size(792, 612), renderer.PageSizes[0]);
    }

    [Fact]
    public async Task Render_ProducesAPngAtTheRequestedWidth()
    {
        using PdfRenderer renderer = await PdfRenderer.OpenAsync(Temp(SamplePdf.TwoLines()));
        using IRandomAccessStream png = await renderer.RenderAsync(1, 300);

        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(png);

        // The engine works in whole pixels before its DPI factor, so allow one pixel of rounding.
        Assert.Equal(BitmapDecoder.PngDecoderId, decoder.DecoderInformation.CodecId);
        Assert.InRange(decoder.PixelWidth, 299u, 301u);
        Assert.InRange(decoder.PixelHeight, 387u, 389u);
    }

    [Fact]
    public async Task Render_DrawsSomethingOnAPageWithText()
    {
        using PdfRenderer renderer = await PdfRenderer.OpenAsync(Temp(SamplePdf.TwoLines()));
        using IRandomAccessStream png = await renderer.RenderAsync(1, 200);

        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(png);
        PixelDataProvider pixels = await decoder.GetPixelDataAsync();
        byte[] data = pixels.DetachPixelData();

        // Text ink makes at least some pixels non-white.
        Assert.Contains(data.Where((_, i) => (i % 4) != 3), value => value < 128);
    }

    [Fact]
    public async Task Render_CapsTheWidth()
    {
        using PdfRenderer renderer = await PdfRenderer.OpenAsync(Temp(SamplePdf.Blank()));
        using IRandomAccessStream png = await renderer.RenderAsync(1, 100000);

        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(png);

        Assert.InRange(decoder.PixelWidth, PdfRenderer.MaxPixelWidth - 2, PdfRenderer.MaxPixelWidth + 2);
    }

    [Fact]
    public async Task Render_CanRunForEveryPageInTurn()
    {
        using PdfRenderer renderer = await PdfRenderer.OpenAsync(Temp(SamplePdf.ThreePages()));

        IRandomAccessStream[] pages = await Task.WhenAll(Enumerable.Range(1, 3).Select(n => renderer.RenderAsync(n, 100)));

        Assert.All(pages, page => Assert.True(page.Size > 0));
        foreach (IRandomAccessStream page in pages)
        {
            page.Dispose();
        }
    }

    [Fact]
    public async Task Render_HonorsCancellation()
    {
        using PdfRenderer renderer = await PdfRenderer.OpenAsync(Temp(SamplePdf.TwoLines()));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => renderer.RenderAsync(1, 300, cancelled.Token));
    }

    [Fact]
    public async Task Open_MissingFile_Throws()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => PdfRenderer.OpenAsync(Path.Combine(Path.GetTempPath(), "does-not-exist.pdf")));
    }

    [Fact]
    public async Task Open_NotAPdf_Throws()
    {
        string path = Temp("this is not a pdf"u8.ToArray());

        await Assert.ThrowsAnyAsync<Exception>(() => PdfRenderer.OpenAsync(path));
    }
}
