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

    /// <summary>The first line under the thumbnail, always there: "Page 3".</summary>
    public string PageLabel => $"Page {PageNumber}";

    /// <summary>The second line under the thumbnail: the page's name from the document outline, or empty.</summary>
    public string Title => session.TextLayer?.GetPageName(PageNumber)?.Trim() ?? string.Empty;

    /// <summary>The second line only shows for a page the outline names.</summary>
    public Microsoft.UI.Xaml.Visibility TitleVisibility
        => string.IsNullOrEmpty(Title) ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

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
