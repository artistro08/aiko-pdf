using AikoPdf.Controls;
using AikoPdf.Pdf;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;
using ZoomMode = AikoPdf.Pdf.ZoomMode;

namespace AikoPdf.Pages;

/// <summary>
/// Reads one document: a status bar for pages and zoom, a hideable thumbnail sidebar, and every page stacked in
/// one continuous scroll.
///
/// Pages are plain elements in a StackPanel rather than a virtualized list, so scroll offsets are exact and
/// jumping to a page is a single ChangeView. Each page only holds a bitmap while it is near the viewport, which
/// keeps memory flat on long documents.
/// </summary>
/// <remarks>
/// @author Devin Green (Artistro08)
/// @link https://learn.microsoft.com/en-us/windows/apps/design/controls/scroll-controls
/// </remarks>
public sealed partial class ViewerPage : Page
{
    // Must match the StackPanel's Padding in XAML; fit modes leave this much room around a page.
    private const double PagePadding = 24;

    // Windows virtual key codes for the "+" and "-" keys on the main keyboard, which VirtualKey doesn't name.
    private const VirtualKey OemPlus  = (VirtualKey)187;
    private const VirtualKey OemMinus = (VirtualKey)189;

    // One mouse wheel notch with Ctrl held changes the zoom by this factor.
    private const double WheelZoomStep = 1.1;

    // What the zoom dropdown offers: the two fit modes, then the preset levels.
    private static readonly ZoomOption[] ZoomOptions =
    [
        new("Fit width", ZoomMode.FitWidth, 0),
        new("Fit page", ZoomMode.FitPage, 0),
        .. Zoom.Presets.Select(p => new ZoomOption(Zoom.FormatPercent(p), ZoomMode.Custom, p)),
    ];

    private readonly List<PdfPageView> pages = [];

    private PdfSession   session = null!;
    private ZoomMode     zoomMode = App.Settings.DefaultFitMode;
    private double       scale = 1;
    private int          currentPage = 1;

    // Where the reader is: the page under a point a third of the way down the viewport (matching the ScrollViewer's
    // VerticalAnchorRatio) and how far into that page the point sits. The page carrying the anchor marker is the
    // one the ScrollViewer holds still when every page changes size.
    private const double AnchorRatio = 0.33;
    private PdfPageView? anchorPage;

    /// <summary>Slides the sidebar island out to the left while its column closes, and back in when it opens.</summary>
    public Microsoft.UI.Xaml.Media.TranslateTransform SidebarSlide { get; } = new();
    private PdfPageView? selectionPage;
    private int          renderedPages;
    private ScrollFade?  thumbnailFade;
    private Microsoft.UI.Xaml.Media.Animation.Storyboard? coverFade;
    private bool         sidebarOpen = true;
    private Microsoft.UI.Xaml.Media.Animation.Storyboard? sidebarSlide;
    private bool         syncingThumbnail;
    private bool         syncingZoom;
    private bool         sidebarHovered;
    private bool         scrollingFromSidebar;

    /// <summary>Builds the page; the document arrives through navigation.</summary>
    public ViewerPage()
    {
        InitializeComponent();
        ZoomCombo.ItemsSource = ZoomOptions;
        Loaded += OnLoaded;
    }

    /// <summary>
    /// Counts pages as their first bitmap lands and lifts the loading cover once enough are in: ten, or every page
    /// of a shorter document. By then the pages right around the reader are ready and the rest fill in far ahead
    /// of any scrolling.
    /// </summary>
    private void OnPageFirstRendered(PdfPageView page)
    {
        const int PagesBeforeReading = 10;

        renderedPages++;
        if ((LoadingCover.Visibility == Visibility.Collapsed) || (renderedPages < Math.Min(PagesBeforeReading, pages.Count)))
        {
            return;
        }

        var fade = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            To       = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(200)),
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fade, LoadingCover);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fade, "Opacity");

        coverFade = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        coverFade.Children.Add(fade);
        coverFade.Completed += (_, _) => LoadingCover.Visibility = Visibility.Collapsed;
        coverFade.Begin();
    }

    /// <summary>Receives the open document and lays out its pages.</summary>
    /// <param name="e">Carries the <see cref="PdfSession"/> as its parameter.</param>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        session = (PdfSession)e.Parameter;

        App.Window.AttachViewer(this);

        for (int n = 1; n <= session.PageCount; n++)
        {
            var view = new PdfPageView(session, n);
            view.SelectionStarted += OnSelectionStarted;
            view.FirstRendered    += OnPageFirstRendered;
            pages.Add(view);
            PageStack.Children.Add(view);
        }

        GoToBox.ItemsSource = Enumerable.Range(1, session.PageCount).ToList();
        UpdatePageText();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        double displayScale = XamlRoot?.RasterizationScale ?? 1;
        ThumbnailList.ItemsSource = Enumerable.Range(1, session.PageCount).Select(n => new ThumbnailItem(session, n, displayScale)).ToList();

        // Top and bottom of the page list fade out while there is more to scroll to that way.
        if ((thumbnailFade is null) && (FindScrollViewer(ThumbnailList) is ScrollViewer thumbnails))
        {
            thumbnailFade = ScrollFade.Attach(ThumbnailList, thumbnails, ThumbnailFade, topLength: 28, bottomLength: 28);
        }

        AddAccelerator(VirtualKey.C, VirtualKeyModifiers.Control, () => selectionPage?.CopySelection());
        AddAccelerator(VirtualKey.A, VirtualKeyModifiers.Control, () => pages[currentPage - 1].SelectAll());
        AddAccelerator(VirtualKey.Add, VirtualKeyModifiers.Control, ZoomIn);
        AddAccelerator(OemPlus, VirtualKeyModifiers.Control, ZoomIn);
        AddAccelerator(VirtualKey.Subtract, VirtualKeyModifiers.Control, ZoomOut);
        AddAccelerator(OemMinus, VirtualKeyModifiers.Control, ZoomOut);
        AddAccelerator(VirtualKey.Number0, VirtualKeyModifiers.Control, () => SetZoomMode(ZoomMode.FitPage));
        AddAccelerator(VirtualKey.Number1, VirtualKeyModifiers.Control, () => SetCustomScale(1.0));
        AddAccelerator(VirtualKey.Number2, VirtualKeyModifiers.Control, () => SetZoomMode(ZoomMode.FitWidth));

        ApplyZoom(snapToPage: true);
        UpdateCurrentPage();
        Scroller.Focus(FocusState.Programmatic);
    }

    private void AddAccelerator(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, args) =>
        {
            args.Handled = true;
            action();
        };
        KeyboardAccelerators.Add(accelerator);
    }

    // =========================================================================
    // ZOOM
    // =========================================================================

    /// <summary>Recomputes the scale for the current fit mode against the viewport, then applies it.</summary>
    /// <param name="snapToPage">True to land on the top of the current page (a chosen fit mode); false to keep the reading position (a resize).</param>
    private void ApplyZoom(bool snapToPage)
    {
        Size   largest  = session.LargestPage;
        double viewport = (Scroller.ViewportWidth > 0) ? Scroller.ViewportWidth : Scroller.ActualWidth;
        double height   = (Scroller.ViewportHeight > 0) ? Scroller.ViewportHeight : Scroller.ActualHeight;

        SetScale(Zoom.Fit(zoomMode, largest.Width, largest.Height, viewport, height, PagePadding, scale), anchorY: null, snapToPage);
        UpdateZoomCombo();
    }

    /// <summary>
    /// Applies a scale to every page and puts the view back where it belongs:
    /// with an anchor (the pointer during wheel zoom) whatever sits under it stays put; with snap, the top of the
    /// current page; otherwise the same spot within the same page, which is what keeps a window resize from
    /// jumping between pages.
    /// </summary>
    /// <param name="newScale">The scale to apply.</param>
    /// <param name="anchorY">Viewport y position to hold still, or null.</param>
    /// <param name="snapToPage">Without an anchor: true snaps to the current page's top, false keeps the position.</param>
    private void SetScale(double newScale, double? anchorY, bool snapToPage = true)
    {
        if (Math.Abs(newScale - scale) < 0.0001)
        {
            return;
        }

        // A resize keeps the reading spot through the ScrollViewer's scroll anchoring (the marker placed by
        // RememberAnchor), which moves the offset in the same layout pass as the page sizes. Wheel and slider zoom
        // and a chosen fit mode are discrete jumps, so they set the offset themselves; the marker comes off first
        // or the two adjustments would stack and throw the view down the page.
        double offset = Scroller.VerticalOffset;
        bool   resize = !snapToPage && (anchorY is null);
        if (!resize)
        {
            anchorPage?.SetAnchor(null);
            anchorPage = null;
        }

        // Where the held point sits in the document: which page, and how far down it. Scaling the raw offset
        // instead would count the padding and the gaps between pages as if they zoomed too, which walks the view
        // several pages down a long document.
        (int held, double fraction) = (anchorY is { } held_y) ? PageFractionAt(offset + held_y) : (0, 0);

        scale = newScale;
        foreach (PdfPageView page in pages)
        {
            page.SetScale(scale);
        }

        if (resize)
        {
            return;
        }

        Scroller.UpdateLayout();
        double target = (anchorY is { } y)
            ? pages[held].ActualOffset.Y + (fraction * pages[held].ActualHeight) - y
            : PageTop(currentPage);
        Scroller.ChangeView(null, Math.Max(0, target), null, disableAnimation: true);
    }

    /// <summary>Which page a content position falls in, and how far down that page it sits.</summary>
    /// <param name="contentY">A position in the scrolled content, in device-independent pixels.</param>
    /// <returns>The page's index in <c>pages</c>, and the fraction of its height, clamped to the page.</returns>
    private (int Index, double Fraction) PageFractionAt(double contentY)
    {
        int index = pages.Count - 1;
        for (int i = 0; i < pages.Count; i++)
        {
            if (contentY < pages[i].ActualOffset.Y + pages[i].ActualHeight)
            {
                index = i;
                break;
            }
        }

        PdfPageView page = pages[index];
        double fraction  = (page.ActualHeight > 0) ? Math.Clamp((contentY - page.ActualOffset.Y) / page.ActualHeight, 0, 1) : 0;
        return (index, fraction);
    }

    /// <summary>Puts the anchor marker on the page under a content offset, at that offset's fraction of the page.</summary>
    /// <param name="contentY">A position in the scrolled content, in device-independent pixels.</param>
    private void MarkAnchor(double contentY)
    {
        if (pages.Count == 0)
        {
            return;
        }

        PdfPageView target = pages[^1];
        foreach (PdfPageView page in pages)
        {
            if (contentY < page.ActualOffset.Y + page.ActualHeight)
            {
                target = page;
                break;
            }
        }

        if (!ReferenceEquals(anchorPage, target))
        {
            anchorPage?.SetAnchor(null);
            anchorPage = target;
        }

        target.SetAnchor((target.ActualHeight > 0) ? Math.Clamp((contentY - target.ActualOffset.Y) / target.ActualHeight, 0, 1) : 0);
    }

    /// <summary>Records where the reader is, from the live scroll offset.</summary>
    private void RememberAnchor()
        => MarkAnchor(Scroller.VerticalOffset + (Scroller.ViewportHeight * AnchorRatio));

    private void SetZoomMode(ZoomMode mode)
    {
        zoomMode = mode;
        ApplyZoom(snapToPage: true);
    }

    private void SetCustomScale(double newScale, double? anchorY = null)
    {
        zoomMode = ZoomMode.Custom;
        SetScale(Zoom.Clamp(newScale), anchorY);
        UpdateZoomCombo();
    }

    private void ZoomIn() => SetCustomScale(Zoom.StepIn(scale));

    private void ZoomOut() => SetCustomScale(Zoom.StepOut(scale));

    /// <summary>Shows the current mode or level in the dropdown (or the exact percentage) and moves the slider to match.</summary>
    private void UpdateZoomCombo()
    {
        syncingZoom = true;
        try
        {
            ZoomCombo.PlaceholderText = Zoom.FormatPercent(scale);
            ZoomCombo.SelectedItem    = ZoomOptions.FirstOrDefault(o => (o.Mode == zoomMode) && ((o.Mode != ZoomMode.Custom) || (Math.Abs(o.Scale - scale) < 0.0001)));
            ZoomSlider.Value          = Zoom.SliderFromScale(scale);
        }
        finally
        {
            syncingZoom = false;
        }
    }

    private void OnZoomSliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (syncingZoom)
        {
            return;
        }

        // Keep the middle of the view in place while the thumb moves, like wheel zoom does.
        SetCustomScale(Zoom.ScaleFromSlider(e.NewValue), anchorY: Scroller.ViewportHeight / 2);
    }

    private void OnZoomInClick(object sender, RoutedEventArgs e) => ZoomIn();

    private void OnSettingsClick(object sender, RoutedEventArgs e) => _ = App.Window.ShowSettingsAsync();

    private void OnHomeClick(object sender, RoutedEventArgs e) => App.Window.ShowHome();

    private void OnZoomOutClick(object sender, RoutedEventArgs e) => ZoomOut();

    private void OnZoomComboChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncingZoom || (ZoomCombo.SelectedItem is not ZoomOption option))
        {
            return;
        }

        if (option.Mode == ZoomMode.Custom)
        {
            SetCustomScale(option.Scale);
        }
        else
        {
            SetZoomMode(option.Mode);
        }

        Scroller.Focus(FocusState.Programmatic);
    }

    private void OnScrollerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (zoomMode != ZoomMode.Custom)
        {
            ApplyZoom(snapToPage: false);
        }
    }

    private void OnPageStackWheel(object sender, PointerRoutedEventArgs e)
    {
        bool control = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        if (!control)
        {
            return;
        }

        PointerPoint point = e.GetCurrentPoint(Scroller);
        double       notches = point.Properties.MouseWheelDelta / 120.0;
        if (notches != 0)
        {
            SetCustomScale(scale * Math.Pow(WheelZoomStep, notches), anchorY: point.Position.Y);
        }

        e.Handled = true;
    }

    // =========================================================================
    // PAGE NAVIGATION
    // =========================================================================

    /// <summary>Scrolls so the given page starts at the top of the viewport, with its gap above still showing.</summary>
    private void GoToPage(int pageNumber)
    {
        Scroller.ChangeView(null, PageTop(Math.Clamp(pageNumber, 1, pages.Count)), null);
    }

    /// <summary>The scroll offset that puts a page's top edge flush with the top of the view.</summary>
    private double PageTop(int pageNumber)
        => Math.Max(0, pages[pageNumber - 1].ActualOffset.Y);

    /// <summary>Works out which page sits a third of the way down the viewport and reflects it in the status bar and sidebar.</summary>
    private void UpdateCurrentPage()
    {
        RememberAnchor();

        double probe = Scroller.VerticalOffset + (Scroller.ViewportHeight / 3);
        int    found = pages.Count;
        for (int i = 0; i < pages.Count; i++)
        {
            if (probe < pages[i].ActualOffset.Y + pages[i].ActualHeight)
            {
                found = i + 1;
                break;
            }
        }

        if (found == currentPage)
        {
            return;
        }

        currentPage = found;
        UpdatePageText();

        // A jump started from the sidebar already has the right thumbnail selected and in view; following the
        // document's animated scroll would flick the selection through every page in between. Likewise, while the
        // pointer is over the sidebar, the user is driving it, so the list stays where they put it.
        if (scrollingFromSidebar)
        {
            return;
        }

        syncingThumbnail = true;
        try
        {
            ThumbnailList.SelectedIndex = currentPage - 1;
        }
        finally
        {
            syncingThumbnail = false;
        }

        if (!sidebarHovered)
        {
            RevealThumbnail(currentPage - 1);
        }
    }

    /// <summary>
    /// Scrolls the sidebar just enough to show a thumbnail whole, keeping the list's own padding above or below
    /// it. ScrollIntoView would put the item flush against the edge. Does nothing when it is already in view.
    /// </summary>
    /// <param name="index">Which thumbnail to show.</param>
    /// <param name="retry">
    /// True on the first call. A thumbnail far down a long document has no container until the list scrolls to it,
    /// so that call asks the list to bring it in and comes back once; it never asks again, because a hidden
    /// sidebar can never realize one and the retry would run every frame for the life of the document.
    /// </param>
    private void RevealThumbnail(int index, bool retry = true)
    {
        if (SidebarHost.Visibility != Visibility.Visible)
        {
            return;
        }

        if (ThumbnailList.ContainerFromIndex(index) is not FrameworkElement container)
        {
            if (!retry)
            {
                return;
            }

            ThumbnailList.ScrollIntoView(ThumbnailList.Items[index], ScrollIntoViewAlignment.Leading);
            DispatcherQueue.TryEnqueue(() => RevealThumbnail(index, retry: false));
            return;
        }

        if (FindScrollViewer(ThumbnailList) is not ScrollViewer scroller)
        {
            return;
        }

        double gap    = ThumbnailList.Padding.Top;
        double top    = container.TransformToVisual(scroller).TransformPoint(new Point(0, 0)).Y;
        double bottom = top + container.ActualHeight;
        double height = scroller.ViewportHeight;

        double? target = null;
        if (top < gap)
        {
            target = scroller.VerticalOffset + top - gap;
        }
        else if (bottom > height - gap)
        {
            target = scroller.VerticalOffset + (bottom - height) + gap;
        }

        // Animated, so the list glides to the new page instead of jumping. The target is clamped to the list's
        // real range first: an animated move aimed past either end is what overshot and sprang back before.
        if (target is { } offset)
        {
            scroller.ChangeView(null, Math.Clamp(offset, 0, scroller.ScrollableHeight), null, disableAnimation: false);
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer found)
            {
                return found;
            }

            if (FindScrollViewer(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!e.IsIntermediate)
        {
            scrollingFromSidebar = false;
        }

        UpdateCurrentPage();
    }

    private void OnPreviousClick(object sender, RoutedEventArgs e) => GoToPage(currentPage - 1);

    private void OnNextClick(object sender, RoutedEventArgs e) => GoToPage(currentPage + 1);

    private void UpdatePageText()
    {
        PageText.Text = $"{currentPage} of {session.PageCount}";
    }

    private void OnGoToFlyoutOpened(object? sender, object e)
    {
        GoToBox.SelectedItem = null;
        GoToBox.Text         = string.Empty;
        GoToBox.Focus(FocusState.Programmatic);
    }

    private void OnGoToSubmitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args)
    {
        // Typed text: jump if it is a number, and never let it become a new list entry.
        args.Handled = true;
        if (int.TryParse(args.Text, out int typed))
        {
            GoToPage(typed);
            CloseGoTo();
        }
    }

    private void OnGoToSelected(object sender, SelectionChangedEventArgs e)
    {
        if (GoToBox.SelectedItem is int picked)
        {
            GoToPage(picked);
            CloseGoTo();
        }
    }

    private void CloseGoTo()
    {
        GoToFlyout.Hide();
        Scroller.Focus(FocusState.Programmatic);
    }

    private void OnThumbnailSelected(object sender, SelectionChangedEventArgs e)
    {
        if (syncingThumbnail || (ThumbnailList.SelectedIndex < 0))
        {
            return;
        }

        scrollingFromSidebar = true;
        currentPage          = ThumbnailList.SelectedIndex + 1;
        UpdatePageText();
        GoToPage(currentPage);
    }

    private void OnThumbnailListPointerEntered(object sender, PointerRoutedEventArgs e) => sidebarHovered = true;

    private void OnThumbnailListPointerExited(object sender, PointerRoutedEventArgs e) => sidebarHovered = false;

    private void OnPageStackPressed(object sender, PointerRoutedEventArgs e)
    {
        // Clicking a page takes focus off the status bar boxes so Page Up/Down and Ctrl shortcuts reach the document.
        Scroller.Focus(FocusState.Programmatic);
    }

    // =========================================================================
    // SELECTION AND SIDEBAR
    // =========================================================================

    private void OnSelectionStarted(PdfPageView page)
    {
        // One selection at a time across the document.
        // ponytail: a drag can't continue across a page boundary. Add cross-page ranges if readers ask for it.
        if (!ReferenceEquals(selectionPage, page))
        {
            selectionPage?.ClearSelection();
            selectionPage = page;
        }
    }

    /// <summary>Shows or hides the page thumbnail sidebar. Fit modes re-measure against the new viewport width.</summary>
    public void ToggleSidebar()
    {
        const double hostWidth = 220;

        // The intent is kept in a field, not read back out of Visibility: the animation sets Visibility straight
        // away, so a second click while it is still running would otherwise animate the same direction twice.
        sidebarOpen = !sidebarOpen;
        bool open   = sidebarOpen;
        sidebarSlide?.Stop();

        // The column narrows so the document re-centers smoothly, while the island itself slides out of view.
        SidebarHost.Visibility = Visibility.Visible;
        var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        storyboard.Children.Add(SlideAnimation(SidebarHost, "Width", open ? 0 : hostWidth, open ? hostWidth : 0));
        storyboard.Children.Add(SlideAnimation(SidebarSlide, "X", open ? -hostWidth : 0, open ? 0 : -hostWidth));
        storyboard.Completed += (_, _) =>
        {
            if (!open)
            {
                SidebarHost.Visibility = Visibility.Collapsed;
            }
        };

        sidebarSlide = storyboard;
        storyboard.Begin();
    }

    /// <summary>A short eased animation. Width is a layout property, so the animation has to be marked dependent.</summary>
    private static Microsoft.UI.Xaml.Media.Animation.DoubleAnimation SlideAnimation(DependencyObject target, string property, double from, double to)
    {
        var animation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From                      = from,
            To                        = to,
            Duration                  = new Duration(TimeSpan.FromMilliseconds(200)),
            EnableDependentAnimation  = true,
            EasingFunction            = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut },
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animation, target);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animation, property);
        return animation;
    }

    /// <summary>One entry in the zoom dropdown.</summary>
    /// <param name="Label">What the dropdown shows.</param>
    /// <param name="Mode">Fit mode, or <see cref="ZoomMode.Custom"/> for a fixed level.</param>
    /// <param name="Scale">The level for custom entries; unused for fit modes.</param>
    private sealed record ZoomOption(string Label, ZoomMode Mode, double Scale)
    {
        /// <inheritdoc/>
        public override string ToString() => Label;
    }
}
