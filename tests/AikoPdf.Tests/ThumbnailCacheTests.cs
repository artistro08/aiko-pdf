using AikoPdf.Pdf;
using AikoPdf.Services;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Xunit;

namespace AikoPdf.Tests;

public sealed class ThumbnailCacheTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), $"aikopdf-thumbs-{Guid.NewGuid():N}");
    private readonly List<string> tempFiles = [];

    public void Dispose()
    {
        foreach (string path in tempFiles)
        {
            File.Delete(path);
        }

        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void PathFor_IsStableAndCaseInsensitive()
    {
        string a = ThumbnailCache.PathFor(@"C:\docs\a.pdf", folder);
        string b = ThumbnailCache.PathFor(@"c:\DOCS\A.PDF", folder);
        string c = ThumbnailCache.PathFor(@"C:\docs\b.pdf", folder);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.StartsWith(folder, a);
        Assert.EndsWith(".png", a);
    }

    [Fact]
    public async Task Save_WritesADecodablePngAtTheDisplayWidth()
    {
        string pdf = SamplePdf.WriteTemp(SamplePdf.TwoLines());
        tempFiles.Add(pdf);
        using PdfRenderer renderer = await PdfRenderer.OpenAsync(pdf);

        string written = await ThumbnailCache.SaveAsync(renderer, pdf, 1.0, folder);

        Assert.Equal(ThumbnailCache.PathFor(pdf, folder), written);
        Assert.True(File.Exists(written));
        Assert.False(File.Exists(written + ".tmp"));

        using IRandomAccessStream png = File.OpenRead(written).AsRandomAccessStream();
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(png);
        Assert.InRange(decoder.PixelWidth, (uint)ThumbnailCache.DisplayWidth - 1, (uint)ThumbnailCache.DisplayWidth + 1);
    }
}
