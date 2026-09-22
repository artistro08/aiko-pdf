using Windows.Data.Pdf;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace AikoPdf.Pdf;

/// <summary>
/// Renders PDF pages to bitmaps with the Windows built-in PDF engine (Windows.Data.Pdf). No third-party renderer,
/// no web view.
///
/// The file is kept open, shared for reading, for as long as the document is. Page sizes are read once at open
/// so the viewer can lay out every page before any is drawn. Renders are serialized because the document reads
/// from one stream.
/// </summary>
/// <remarks>
/// @author Devin Green (Artistro08)
/// @link https://learn.microsoft.com/en-us/uwp/api/windows.data.pdf
/// </remarks>
public sealed class PdfRenderer : IDisposable
{
    /// <summary>Widest bitmap a page is rendered at, so an 800% zoom on a poster page can't exhaust memory.</summary>
    public const uint MaxPixelWidth = 4096;

    // Windows.Data.Pdf reports page sizes in device-independent pixels at 96 DPI, not points.
    private const double PointsPerDip = 72.0 / 96.0;

    private readonly FileStream          file;
    private readonly IRandomAccessStream stream;
    private readonly PdfDocument         document;
    private readonly SemaphoreSlim       renderGate = new(1, 1);
    private readonly double              outputScale;

    private PdfRenderer(FileStream file, IRandomAccessStream stream, PdfDocument document, IReadOnlyList<Size> pageSizes, double outputScale)
    {
        this.file        = file;
        this.stream      = stream;
        this.document    = document;
        this.outputScale = outputScale;
        PageSizes        = pageSizes;
    }

    /// <summary>Every page's rendered size in points (1/72 inch), rotation applied, indexed from zero.</summary>
    public IReadOnlyList<Size> PageSizes { get; }

    /// <summary>Number of pages in the document.</summary>
    public int PageCount => PageSizes.Count;

    /// <summary>Opens a PDF for rendering.</summary>
    /// <param name="path">Full path of the file.</param>
    /// <param name="password">The user password for an encrypted file, or null.</param>
    /// <returns>The open renderer.</returns>
    /// <exception cref="IOException">The file can't be read.</exception>
    /// <exception cref="Exception">The file isn't a PDF Windows can open, or the password is wrong (COM error from the PDF engine).</exception>
    public static async Task<PdfRenderer> OpenAsync(string path, string? password = null)
    {
        var                 file   = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        IRandomAccessStream stream = file.AsRandomAccessStream();
        try
        {
            PdfDocument document = (password is null)
                ? await PdfDocument.LoadFromStreamAsync(stream)
                : await PdfDocument.LoadFromStreamAsync(stream, password);

            var sizes = new List<Size>((int)document.PageCount);
            for (uint i = 0; i < document.PageCount; i++)
            {
                using PdfPage page = document.GetPage(i);
                sizes.Add(new Size(page.Size.Width * PointsPerDip, page.Size.Height * PointsPerDip));
            }

            return new PdfRenderer(file, stream, document, sizes, await MeasureOutputScaleAsync(document));
        }
        catch
        {
            stream.Dispose();
            file.Dispose();
            throw;
        }
    }

    /// <summary>Renders one page as a PNG image.</summary>
    /// <param name="pageNumber">1-based page number.</param>
    /// <param name="pixelWidth">Wanted bitmap width in pixels; height follows the page's aspect ratio. Capped at <see cref="MaxPixelWidth"/>.</param>
    /// <param name="cancellationToken">Cancels a render whose page scrolled out of view.</param>
    /// <returns>A seekable PNG stream positioned at the start. The caller disposes it.</returns>
    public async Task<IRandomAccessStream> RenderAsync(int pageNumber, double pixelWidth, CancellationToken cancellationToken = default)
    {
        Size   size   = PageSizes[pageNumber - 1];
        double wanted = Math.Clamp(pixelWidth, 1, MaxPixelWidth);
        uint   width  = (uint)Math.Max(1, Math.Round(wanted / outputScale));
        uint   height = (uint)Math.Max(1, Math.Round(width * size.Height / size.Width));

        await renderGate.WaitAsync(cancellationToken);
        try
        {
            using PdfPage page = document.GetPage((uint)(pageNumber - 1));
            var options = new PdfPageRenderOptions
            {
                DestinationWidth  = width,
                DestinationHeight = height,
                BackgroundColor   = Windows.UI.Color.FromArgb(255, 255, 255, 255),
            };

            var output = new InMemoryRandomAccessStream();
            try
            {
                await page.RenderToStreamAsync(output, options).AsTask(cancellationToken);
            }
            catch
            {
                output.Dispose();
                throw;
            }

            output.Seek(0);
            return output;
        }
        finally
        {
            renderGate.Release();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        renderGate.Dispose();
        stream.Dispose();
        file.Dispose();
    }

    /// <summary>
    /// The PDF engine multiplies the requested output size by the system DPI scale (125% gives a 375px image for a
    /// 300px request), and nothing reports that factor. One tiny probe render measures it so every later render
    /// comes out at the size asked for.
    /// ponytail: measured once at open against the primary display; a window moved to a monitor with a different
    /// scale still gets correctly sized bitmaps, just not through this factor. Re-measure per render if that shows.
    /// </summary>
    private static async Task<double> MeasureOutputScaleAsync(PdfDocument document)
    {
        const uint probeWidth = 64;
        if (document.PageCount == 0)
        {
            return 1.0;
        }

        using PdfPage page  = document.GetPage(0);
        using var     probe = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(probe, new PdfPageRenderOptions { DestinationWidth = probeWidth, DestinationHeight = probeWidth });
        probe.Seek(0);

        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(probe);
        return (decoder.PixelWidth > 0) ? decoder.PixelWidth / (double)probeWidth : 1.0;
    }
}
