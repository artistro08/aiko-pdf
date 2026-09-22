using System.Globalization;
using AikoPdf.Controls;
using AikoPdf.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AikoPdf.Pages;

/// <summary>
/// What the app shows when no PDF is open: an open button and the recently opened files as preview cards.
/// </summary>
/// <remarks>
/// @author Devin Green (Artistro08)
/// </remarks>
public sealed partial class HomePage : Page
{
    /// <summary>Builds the page and keeps the recent list in sync while it is shown.</summary>
    public HomePage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            App.Recent.Prune();
            App.Recent.Changed += Refresh;
            Refresh();
            SizeRecentPanel(ActualWidth);

            // Clear under the heading, fading in just below it, so a card scrolling up is gone before it gets there.
            fade ??= ScrollFade.Attach(RecentSection, RecentSection, RecentFade, topLength: 24, bottomLength: 32, topHold: 50);
        };
        Unloaded += (_, _) => App.Recent.Changed -= Refresh;
    }

    // Card width (the 120 preview plus the button's padding), the gap between cards, and the panel's inner margin.
    private const double CardWidth    = 136;
    private const double CardSpacing  = 16;
    private const double PanelPadding = 48;

    // Below this page width the panel holds three cards a row instead of four.
    private const double FourAcrossWidth = 900;

    private ScrollFade? fade;

    /// <summary>
    /// Sizes the recent panel to hold exactly four cards a row, or three in a narrow window, so it sits centered
    /// with room around it instead of running to the window's edges.
    /// </summary>
    /// <param name="pageWidth">The page's current width.</param>
    private void SizeRecentPanel(double pageWidth)
    {
        int columns = (pageWidth >= FourAcrossWidth) ? 4 : 3;
        RecentLayout.MaximumRowsOrColumns = columns;
        RecentIsland.Width = (columns * CardWidth) + ((columns - 1) * CardSpacing) + PanelPadding;
    }

    /// <summary>Formats a last-opened time for the card, in the user's short date and time format.</summary>
    /// <param name="when">When the file was opened.</param>
    /// <returns>Text like "9/21/2026 3:04 PM".</returns>
    public static string FormatDate(DateTimeOffset when)
        => when.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);

    /// <summary>The cached first-page preview for a PDF, or null (the card then shows a document icon).</summary>
    /// <param name="pdfPath">Full path of the PDF.</param>
    /// <returns>An image source for the card, or null when no preview has been saved yet.</returns>
    public static ImageSource? Thumbnail(string pdfPath)
    {
        string png = ThumbnailCache.PathFor(pdfPath);
        return File.Exists(png)
            ? new BitmapImage(new Uri(png)) { CreateOptions = BitmapCreateOptions.IgnoreImageCache }
            : null;
    }

    private void Refresh()
    {
        bool empty = App.Recent.Items.Count == 0;

        // With nothing to list, the recent half collapses and the welcome block takes the whole window. Otherwise
        // the welcome block gets the top half, or its own height when the window is too short for that.
        RecentGrid.ItemsSource    = App.Recent.Items.ToList();
        RecentIsland.Visibility   = empty ? Visibility.Collapsed : Visibility.Visible;
        RecentRow.Height          = empty ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        WelcomeRow.Height         = empty ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        WelcomeRow.MinHeight      = empty ? 0 : ActualHeight / 2;
    }

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (RecentRow.Height.IsStar)
        {
            WelcomeRow.MinHeight = e.NewSize.Height / 2;
        }

        SizeRecentPanel(e.NewSize.Width);

        UpdateWindowMinimum();
    }

    /// <summary>
    /// The window may not shrink past what this page needs: the welcome block plus, when there are recents, the
    /// heading and one row of cards. Measured from the live layout so it follows fonts and scale.
    /// </summary>
    private void UpdateWindowMinimum()
    {
        const double titleBar        = 48;
        const double welcomeMargins  = 48;
        const double recentMargins   = 32;
        const double sideMargins     = 64;

        double height = titleBar + WelcomePanel.ActualHeight + welcomeMargins;
        if (RecentRow.Height.IsStar)
        {
            double card = (RecentGrid.TryGetElement(0) as FrameworkElement)?.ActualHeight ?? 0;
            height += recentMargins + RecentHeader.ActualHeight + 36 + card;
        }

        double width = WelcomeButtons.ActualWidth + sideMargins;
        if ((height > 0) && (width > 0))
        {
            App.Window.SetMinimumSize(width, height);
        }
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        _ = App.Window.PickAndOpenAsync();
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => _ = App.Window.ShowSettingsAsync();

    private void OnRecentClick(object sender, RoutedEventArgs e)
    {
        var file = (RecentFile)((FrameworkElement)sender).Tag;
        if (!File.Exists(file.Path))
        {
            Notice.Message = $"\"{file.Name}\" has been moved or deleted, so it was removed from this list.";
            Notice.IsOpen  = true;
            App.Recent.Remove(file.Path);
            return;
        }

        _ = App.Window.OpenFileAsync(file.Path);
    }

    private void OnRecentRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var element = (FrameworkElement)sender;
        var file    = (RecentFile)element.Tag;

        var showInFolder = new MenuFlyoutItem { Text = "Show in folder", Icon = new FontIcon { Glyph = "" } };
        showInFolder.Click += (_, _) => Shell.ShowInFolder(file.Path);

        var remove = new MenuFlyoutItem { Text = "Remove from list", Icon = new SymbolIcon(Symbol.Delete) };
        remove.Click += (_, _) => App.Recent.Remove(file.Path);

        var flyout = new MenuFlyout();
        flyout.Items.Add(showInFolder);
        flyout.Items.Add(remove);
        flyout.ShowAt(element, e.GetPosition(element));
        e.Handled = true;
    }
}
