using System.ComponentModel;
using AikoPdf.Pdf;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace AikoPdf.Controls;

/// <summary>
/// One entry in the page sidebar. The thumbnail bitmap is rendered the first time the list asks for it, so only
/// pages that scroll into the sidebar cost anything.
/// ponytail: rendered thumbnails are kept for the life of the document (about 100 KB each). Drop to an LRU cache
/// if thousand-page files show up.
/// </summary>
/// <remarks>
/// @author Devin Green (Artistro08)
/// </remarks>
public sealed class ThumbnailItem : INotifyPropertyChanged
{
    /// <summary>Thumbnail width in device-independent pixels.</summary>
    public const double DisplayWidth = 140;

    private readonly PdfSession session;
    private readonly double     pixelWidth;
    private BitmapImage?        image;
    private bool                loading;

    /// <summary>Describes one page for the sidebar.</summary>
    /// <param name="session">The open document.</param>
    /// <param name="pageNumber">1-based page number.</param>
    /// <param name="rasterizationScale">The display's scale, so the bitmap is crisp at high DPI.</param>
    public ThumbnailItem(PdfSession session, int pageNumber, double rasterizationScale)
    {
        this.session = session;
        PageNumber   = pageNumber;
        pixelWidth   = DisplayWidth * rasterizationScale;

        Size size = session.PageSizes[pageNumber - 1];
        Height    = DisplayWidth * size.Height / size.Width;
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>1-based page number.</summary>
    public int PageNumber { get; }

    /// <summary>What shows under the thumbnail: "Intro | Page 3" when the outline names the page, else "Page 3".</summary>
    public string Label => FormatLabel(session.TextLayer?.GetPageName(PageNumber), PageNumber);

    /// <summary>Builds the sidebar label for a page.</summary>
    /// <param name="name">The page's outline name, or null.</param>
    /// <param name="pageNumber">1-based page number.</param>
    /// <returns>"Name | Page N" or "Page N".</returns>
    public static string FormatLabel(string? name, int pageNumber)
        => string.IsNullOrWhiteSpace(name) ? $"Page {pageNumber}" : $"{name} | Page {pageNumber}";

    /// <summary>Thumbnail width in device-independent pixels.</summary>
    public double Width => DisplayWidth;

    /// <summary>Thumbnail height in device-independent pixels, from the page's aspect ratio.</summary>
    public double Height { get; }

    /// <summary>The rendered thumbnail. Null until the first read has finished rendering it; then the change is raised.</summary>
    public BitmapImage? Image
    {
        get
        {
            if ((image is null) && !loading)
            {
                loading = true;
                _ = LoadAsync();
            }

            return image;
        }
    }

    private async Task LoadAsync()
    {
        try
        {
            using IRandomAccessStream png = await session.Renderer.RenderAsync(PageNumber, pixelWidth);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(png);
            image = bitmap;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Image)));
        }
        catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
        {
            App.Log($"Thumbnail for page {PageNumber} failed: {ex.Message}");
        }
    }
}
