using UglyToad.PdfPig.Exceptions;
using Windows.Foundation;

namespace AikoPdf.Pdf;

/// <summary>
/// One open document: the Windows renderer that draws its pages and the PdfPig text layer that makes them
/// selectable, opened together from the same file and closed together.
///
/// The text layer is optional. If PdfPig can't parse a file that Windows can still draw, the document opens
/// read-only with no text selection rather than not at all.
/// </summary>
/// <remarks>
/// @author Devin Green (Artistro08)
/// @link https://learn.microsoft.com/en-us/uwp/api/windows.data.pdf.pdfdocument
/// </remarks>
public sealed class PdfSession : IDisposable
{
    private PdfSession(string path, PdfRenderer renderer, PdfTextLayer? textLayer)
    {
        Path      = path;
        Renderer  = renderer;
        TextLayer = textLayer;
    }

    /// <summary>Full path of the open file.</summary>
    public string Path { get; }

    /// <summary>The file name, for the title bar.</summary>
    public string Name => System.IO.Path.GetFileName(Path);

    /// <summary>Draws pages.</summary>
    public PdfRenderer Renderer { get; }

    /// <summary>Provides glyph positions for selection, or null when the file's text couldn't be parsed.</summary>
    public PdfTextLayer? TextLayer { get; }

    /// <summary>Number of pages.</summary>
    public int PageCount => Renderer.PageCount;

    /// <summary>Every page's size in points, rotation applied, indexed from zero.</summary>
    public IReadOnlyList<Size> PageSizes => Renderer.PageSizes;

    /// <summary>The largest width and height over all pages, which fit-to-width and fit-to-page are measured against.</summary>
    public Size LargestPage
        => new(PageSizes.Max(s => s.Width), PageSizes.Max(s => s.Height));

    /// <summary>Opens a PDF for viewing.</summary>
    /// <param name="path">Full path of the file.</param>
    /// <param name="password">The password for an encrypted file, or null to try without one.</param>
    /// <returns>The open session.</returns>
    /// <exception cref="PdfDocumentEncryptedException">The file needs a password, or the one given is wrong.</exception>
    /// <exception cref="IOException">The file can't be read.</exception>
    /// <exception cref="Exception">Windows can't open the file as a PDF (COM error from the PDF engine).</exception>
    public static async Task<PdfSession> OpenAsync(string path, string? password = null)
    {
        // PdfPig goes first because it tells "needs a password" apart from "not a PDF".
        PdfTextLayer? textLayer = null;
        try
        {
            textLayer = await Task.Run(() => PdfTextLayer.Open(path, password));
        }
        catch (PdfDocumentEncryptedException)
        {
            throw;
        }
        catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
        {
            // Windows may still render it; the document just won't have selectable text. That includes a file
            // another process holds open for writing: PdfPig asks for shared read only, the renderer asks for
            // read and write, so the document still opens.
        }

        try
        {
            PdfRenderer renderer = await PdfRenderer.OpenAsync(path, password);
            return new PdfSession(path, renderer, textLayer);
        }
        catch
        {
            textLayer?.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        TextLayer?.Dispose();
        Renderer.Dispose();
    }
}
