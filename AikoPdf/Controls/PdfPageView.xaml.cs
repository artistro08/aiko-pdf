using AikoPdf.Pdf;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace AikoPdf.Controls;

/// <summary>
/// One page in the viewer: sized to the page at the current zoom, with mouse and pen text selection on top.
///
/// The bitmap is rendered above the on-screen pixel size (zoom times display scale times an oversampling
/// factor), so it downscales cleanly: text keeps an even weight while a zoom or resize stretches the old bitmap,
/// and the fresh render that lands once the size has settled is not a visible switch from soft to sharp.
/// A page keeps a bitmap for the life of the document so nothing ever scrolls into view blank or soft: pages near
/// the viewport hold one with extra pixels, the rest hold one at the on-screen size.
/// </summary>
/// <remarks>
/// @author Devin Green (Artistro08)
/// </remarks>
public sealed partial class PdfPageView : UserControl
{
    // Every page keeps a bitmap for the life of the document, so nothing ever scrolls into view blank or soft.
    // Pages within this distance of the viewport (in DIPs) hold one with extra pixels for smooth resizing; the
    // rest hold one at exactly the on-screen size, which is just as sharp standing still.
    private const double NearDistance = 2000;

    // Near bitmaps carry twice the on-screen pixels: downscaling from there averages the text's edges, so a page
    // stretched by a resize looks the same as a fresh render, instead of flipping between thin and bold. Far
    // bitmaps are one to one. A quarter-size far bitmap used to show for a second as a blurred page whenever a
    // reader scrolled faster than the renders could keep up.
    private const double NearOversample = 2.0;
    private const double FarOversample  = 1.0;

    // Far bitmaps stop growing here, so 800% zoom on a long document doesn't hold a poster-sized bitmap for every
    // page. At any ordinary zoom a page is narrower than this and renders at full sharpness.
    // ponytail: a 100-page file at fit width holds roughly 800 MB of far bitmaps; drop far pages past a few
    // hundred pages away if long documents run short of memory.
    private const double FarMaxPixelWidth = 2400;

    // Near pages render before far ones: far renders wait while any near render is in flight.
    private static int nearRendersInFlight;

    private static readonly InputSystemCursor TextCursor  = InputSystemCursor.Create(InputSystemCursorShape.IBeam);
    private static readonly InputSystemCursor ArrowCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);

    private readonly PdfSession           session;
    private readonly Size                 pageSize;
    private readonly DispatcherQueueTimer rerenderTimer;

    private double                   scale = 1;
    private bool                     isNearViewport;
    private double                   renderedPixelWidth;
    private CancellationTokenSource? renderCancellation;
    private IReadOnlyList<TextGlyph>? glyphs;
    private bool                     loadingGlyphs;
    private int                      clickCount;
    private ulong                    lastClickTime;
    private Point                    lastClickPoint;
    private TextSelection?           selection;
    private bool                     dragging;

    /// <summary>Creates the view for one page.</summary>
    /// <param name="session">The open document.</param>
    /// <param name="pageNumber">1-based page number.</param>
    public PdfPageView(PdfSession session, int pageNumber)
    {
        InitializeComponent();
        this.session = session;
        PageNumber   = pageNumber;
        pageSize     = session.PageSizes[pageNumber - 1];

        rerenderTimer             = DispatcherQueue.CreateTimer();
        rerenderTimer.Interval    = TimeSpan.FromMilliseconds(120);
        rerenderTimer.IsRepeating = false;
        rerenderTimer.Tick       += (_, _) => _ = RenderIfNeededAsync();

        SetScale(1);
        EffectiveViewportChanged += OnEffectiveViewportChanged;
        Unloaded                 += (_, _) => Unload();
    }

    /// <summary>Raised when a drag selection starts on this page, so the viewer can clear the selection on any other page.</summary>
    public event EventHandler? SelectionStarted;

    /// <summary>Raised once, when the page shows its first bitmap. The viewer counts these to lift its loading cover.</summary>
    public event EventHandler? FirstRendered;

    /// <summary>True once the page has shown a bitmap.</summary>
    public bool HasRendered { get; private set; }

    /// <summary>1-based page number.</summary>
    public int PageNumber { get; }

    /// <summary>True while some text on this page is selected.</summary>
    public bool HasSelection => selection is { IsEmpty: false };

    /// <summary>The selected text, or an empty string.</summary>
    public string SelectedText => selection?.GetText() ?? string.Empty;

    /// <summary>Resizes the page to the new zoom. The old bitmap is stretched at once and re-rendered shortly after.</summary>
    /// <param name="newScale">Device-independent pixels per PDF point.</param>
    public void SetScale(double newScale)
    {
        scale = newScale;

        // Whole physical pixels: a page that is 1015.5px wide gets its bitmap resampled by half a pixel, which reads
        // as text flipping between bold and thin, sharp and fuzzy, from one size to the next.
        double pixels = XamlRoot?.RasterizationScale ?? 1;
        Width  = Math.Round(pageSize.Width * scale * pixels) / pixels;
        Height = Math.Round(pageSize.Height * scale * pixels) / pixels;
        RedrawSelection();

        // Debounced: one fresh render once the size has settled. The oversampled bitmap keeps its look meanwhile.
        rerenderTimer.Stop();
        rerenderTimer.Start();
    }

    /// <summary>Selects every glyph on the page.</summary>
    public void SelectAll()
    {
        if (selection is null)
        {
            return;
        }

        SelectionStarted?.Invoke(this, EventArgs.Empty);
        selection.SelectAll();
        RedrawSelection();
    }

    /// <summary>Removes the selection.</summary>
    public void ClearSelection()
    {
        selection?.Clear();
        RedrawSelection();
    }

    /// <summary>Puts the selected text on the clipboard. No-op when nothing is selected.</summary>
    public void CopySelection()
    {
        if (!HasSelection)
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(SelectedText);
        Clipboard.SetContent(package);
    }

    // =========================================================================
    // RENDERING
    // =========================================================================

    private void OnEffectiveViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
    {
        isNearViewport = args.BringIntoViewDistanceY <= NearDistance;
        _ = RenderIfNeededAsync();
        if (isNearViewport)
        {
            _ = EnsureGlyphsAsync();
        }
    }

    private void Unload()
    {
        renderCancellation?.Cancel();
        renderCancellation = null;
        PageImage.Source   = null;
        renderedPixelWidth = 0;
        LoadingRing.IsActive = false;
    }

    private async Task RenderIfNeededAsync()
    {
        if (XamlRoot is null)
        {
            return;
        }

        double pixels     = XamlRoot.RasterizationScale;
        double pixelWidth = isNearViewport
            ? Math.Round(Width * pixels * NearOversample)
            : Math.Round(Math.Min(Width * pixels * FarOversample, FarMaxPixelWidth));

        // A far page keeps its small bitmap through zoom changes unless the size moved a lot; a near page
        // re-renders for any change so it is always shown 1:1 or better.
        double change = (renderedPixelWidth > 0) ? Math.Abs(pixelWidth - renderedPixelWidth) / renderedPixelWidth : double.MaxValue;
        if ((isNearViewport && (change < 0.001)) || (!isNearViewport && (change < 0.25)))
        {
            return;
        }

        renderCancellation?.Cancel();
        var  cancellation   = new CancellationTokenSource();
        bool near           = isNearViewport;
        renderCancellation  = cancellation;
        LoadingRing.IsActive = PageImage.Source is null;

        if (near)
        {
            Interlocked.Increment(ref nearRendersInFlight);
        }

        try
        {
            // Far pages yield to near ones so the pages on screen never wait behind the rest of the document.
            while (!near && (Volatile.Read(ref nearRendersInFlight) > 0))
            {
                await Task.Delay(40, cancellation.Token);
            }

            using IRandomAccessStream png = await session.Renderer.RenderAsync(PageNumber, pixelWidth, cancellation.Token);
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(png);
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            PageImage.Source   = bitmap;
            renderedPixelWidth = pixelWidth;
            if (!HasRendered)
            {
                HasRendered = true;
                FirstRendered?.Invoke(this, EventArgs.Empty);
            }

            // The white placeholder is only for the wait before the first render; once the bitmap is in, its own
            // background shows, and no white edge can peek out around a dark page.
            Root.Background = null;
        }
        catch (OperationCanceledException)
        {
            // Scrolled away or zoomed again before this render finished.
        }
        catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
        {
            App.Log($"Render of page {PageNumber} failed: {ex.Message}");
        }
        finally
        {
            if (near)
            {
                Interlocked.Decrement(ref nearRendersInFlight);
            }

            if (ReferenceEquals(renderCancellation, cancellation))
            {
                LoadingRing.IsActive = false;
            }
        }
    }

    // =========================================================================
    // TEXT SELECTION
    // =========================================================================

    private async Task EnsureGlyphsAsync()
    {
        if ((glyphs is not null) || loadingGlyphs || (session.TextLayer is null))
        {
            return;
        }

        loadingGlyphs = true;
        try
        {
            PdfTextLayer textLayer = session.TextLayer;
            int          page      = PageNumber;
            (glyphs, IReadOnlyList<Rect> panels) = await Task.Run(() => (textLayer.GetGlyphs(page), textLayer.GetContainers(page)));
            selection = new TextSelection(glyphs, panels);
        }
        catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
        {
            App.Log($"Text for page {PageNumber} failed: {ex.Message}");
            glyphs = [];
        }
    }

    private Point ToPagePoint(Point position) => new(position.X / scale, position.Y / scale);

    /// <inheritdoc/>
    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        base.OnPointerPressed(e);
        PointerPoint point = e.GetCurrentPoint(this);

        // Touch keeps panning the document; mouse and pen select text.
        if ((selection is null) || !point.Properties.IsLeftButtonPressed || (e.Pointer.PointerDeviceType == PointerDeviceType.Touch))
        {
            return;
        }

        SelectionStarted?.Invoke(this, EventArgs.Empty);
        Point pagePoint = ToPagePoint(point.Position);

        // Presses close together in time and place count up the way Windows counts them: a double-click takes
        // the word, a triple-click the paragraph, and a fourth starts over as a plain press.
        clickCount = IsFollowUpClick(point) ? ((clickCount % 3) + 1) : 1;
        lastClickTime  = point.Timestamp;
        lastClickPoint = point.Position;

        switch (clickCount)
        {
            case 2:
                selection.SelectWord(pagePoint);
                RedrawSelection();
                return;

            case 3:
                selection.SelectParagraph(pagePoint);
                RedrawSelection();
                return;

            default:
                selection.Begin(pagePoint);
                RedrawSelection();
                dragging = CapturePointer(e.Pointer);
                return;
        }
    }

    /// <summary>
    /// True when a press lands soon enough after the last one, and near enough to it, to count as the next click
    /// of a double or triple click. Both limits come from the user's own mouse settings.
    /// </summary>
    /// <param name="point">The press being counted.</param>
    /// <returns>True for a follow-up click.</returns>
    private bool IsFollowUpClick(PointerPoint point)
    {
        const int DoubleClickWidth  = 36;
        const int DoubleClickHeight = 37;

        // Pointer timestamps are in microseconds; the system double-click time is in milliseconds.
        ulong  elapsed = (point.Timestamp - lastClickTime) / 1000;
        double scale   = XamlRoot?.RasterizationScale ?? 1;
        double dx      = Math.Abs(point.Position.X - lastClickPoint.X) * scale;
        double dy      = Math.Abs(point.Position.Y - lastClickPoint.Y) * scale;

        return (clickCount > 0)
            && (elapsed <= NativeMethods.GetDoubleClickTime())
            && (dx <= NativeMethods.GetSystemMetrics(DoubleClickWidth) / 2.0)
            && (dy <= NativeMethods.GetSystemMetrics(DoubleClickHeight) / 2.0);
    }

    /// <inheritdoc/>
    protected override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        base.OnPointerMoved(e);
        Point pagePoint = ToPagePoint(e.GetCurrentPoint(this).Position);

        if (dragging && (selection is not null))
        {
            selection.Extend(pagePoint);
            RedrawSelection();
            return;
        }

        // The whole page reads as selectable text, like any text view, once its glyphs are known.
        ProtectedCursor = (glyphs is { Count: > 0 }) ? TextCursor : ArrowCursor;
    }

    /// <inheritdoc/>
    protected override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        base.OnPointerReleased(e);
        EndDrag(e.Pointer);
    }

    /// <inheritdoc/>
    protected override void OnPointerCanceled(PointerRoutedEventArgs e)
    {
        base.OnPointerCanceled(e);
        EndDrag(e.Pointer);
    }

    /// <inheritdoc/>
    protected override void OnPointerCaptureLost(PointerRoutedEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        dragging = false;
    }

    private void EndDrag(Pointer pointer)
    {
        if (!dragging)
        {
            return;
        }

        dragging = false;
        ReleasePointerCapture(pointer);
    }

    private void RedrawSelection()
    {
        SelectionLayer.Children.Clear();
        if (selection is null)
        {
            return;
        }

        var fill = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        foreach (Rect rect in selection.GetRects())
        {
            var highlight = new Rectangle
            {
                Width   = rect.Width * scale,
                Height  = rect.Height * scale,
                Fill    = fill,
                Opacity = 0.35,
            };
            Canvas.SetLeft(highlight, rect.X * scale);
            Canvas.SetTop(highlight, rect.Y * scale);
            SelectionLayer.Children.Add(highlight);
        }
    }

    private void OnCopyClick(object sender, RoutedEventArgs e) => CopySelection();

    private void OnSelectAllClick(object sender, RoutedEventArgs e) => SelectAll();
}
