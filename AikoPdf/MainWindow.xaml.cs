using AikoPdf.Pages;
using AikoPdf.Pdf;
using AikoPdf.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using UglyToad.PdfPig.Exceptions;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using ZoomMode = AikoPdf.Pdf.ZoomMode;

namespace AikoPdf;

/// <summary>
/// The one app window. Owns the open document, the title bar and switches between the home page (no document)
/// and the viewer. Opening a file, from the picker, a drop, the recent list or the command line, all goes through
/// <see cref="OpenFileAsync"/>.
/// </summary>
/// <remarks>
/// @author Devin Green (Artistro08)
/// @link https://learn.microsoft.com/en-us/windows/apps/develop/title-bar
/// </remarks>
public sealed partial class MainWindow : Window
{
    private const string AppName = "Aiko";

    // How long the home page takes to appear when it follows a dialog rather than a reader's own click.
    private const int FadeMilliseconds = 250;

    private PdfSession?   current;
    private ViewerPage?   viewer;
    private SubclassProc? activationHook;
    private bool          opening;
    private bool          sized;
    private bool          opened;
    private bool          printing;

    /// <summary>
    /// Builds the window with a Mica backdrop and the WinUI title bar, then shows the home page. Pass a file to
    /// open it straight away instead: a document opened from Explorer should never flash the home page first.
    /// </summary>
    /// <param name="startupFile">A PDF to open as the window appears, or null for the home page.</param>
    public MainWindow(string? startupFile = null)
    {
        InitializeComponent();
        SystemBackdrop             = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, @"Assets\Aiko.ico"));
        Root.ActualThemeChanged += (_, _) => ApplyCaptionColors();
        ApplyCaptionColors();
        RestoreWindowSize();
        HookActivation();
        Closed += (_, _) =>
        {
            SaveWindowSize();
            current?.Dispose();
        };
        AppWindow.Changed += (_, _) => SizeCaptionSpacer();
        Root.Loaded       += (_, _) =>
        {
            SizeCaptionSpacer();

            // Sized again now the window is on a monitor and reports that monitor's scaling. Before it is shown
            // it answers with the system default, which opened it four fifths of its size on a 125% display.
            if (!sized)
            {
                sized = true;
                RestoreWindowSize();
            }
        };
        if (startupFile is null)
        {
            ShowHome();
        }
        else
        {
            // The frame stays empty until the document is ready, which is a moment of the window's own backdrop
            // rather than a home page that was never asked for.
            Title = System.IO.Path.GetFileName(startupFile);
            Root.Loaded += async (_, _) =>
            {
                // Loaded can fire again if the content is ever re-attached; the file is opened once.
                if (opened)
                {
                    return;
                }

                opened = true;
                await OpenFileAsync(startupFile);

                // A file that could not be opened (wrong password, damaged, gone) leaves an empty frame, so fall
                // back to the page the app would have shown anyway. It fades in, because it arrives as an answer
                // to the dialog the reader just dismissed rather than as the page they asked for.
                if (RootFrame.Content is null)
                {
                    ShowHome();
                    FadeIn(RootFrame);
                }
            };
        }
    }

    /// <summary>
    /// Colors the caption buttons (minimize, maximize, close) for the current theme. They are drawn by the window
    /// frame, not XAML, so they don't follow theme brushes on their own and stayed white in light mode.
    /// </summary>
    private void ApplyCaptionColors()
    {
        bool dark = Root.ActualTheme == ElementTheme.Dark;
        Windows.UI.Color foreground = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        Windows.UI.Color hover      = dark ? Windows.UI.Color.FromArgb(25, 255, 255, 255) : Windows.UI.Color.FromArgb(25, 0, 0, 0);
        Windows.UI.Color pressed    = dark ? Windows.UI.Color.FromArgb(40, 255, 255, 255) : Windows.UI.Color.FromArgb(40, 0, 0, 0);
        Windows.UI.Color inactive   = dark ? Windows.UI.Color.FromArgb(255, 160, 160, 160) : Windows.UI.Color.FromArgb(255, 120, 120, 120);

        AppWindowTitleBar bar = AppWindow.TitleBar;
        bar.ButtonForegroundColor         = foreground;
        bar.ButtonHoverForegroundColor    = foreground;
        bar.ButtonPressedForegroundColor  = foreground;
        bar.ButtonInactiveForegroundColor = inactive;
        bar.ButtonBackgroundColor         = Microsoft.UI.Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        bar.ButtonHoverBackgroundColor    = hover;
        bar.ButtonPressedBackgroundColor  = pressed;
    }

    /// <summary>Applies the minimum size and brings back the size (or maximized state) from the last run.</summary>
    private void RestoreWindowSize()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter)
        {
            return;
        }

        // Sizes are stored in device-independent pixels, so the window comes back the size it looked even on a
        // monitor with different scaling.
        double scale = DisplayScale;
        (int width, int height) = App.Settings.HasSize
            ? (App.Settings.Width, App.Settings.Height)
            : (AppSettings.DefaultWidth, AppSettings.DefaultHeight);
        Resize((int)Math.Round(width * scale), (int)Math.Round(height * scale));

        if (App.Settings.IsMaximized)
        {
            presenter.Maximize();
        }
    }

    /// <summary>
    /// Sets the smallest size the window may be dragged to, from what the home page needs to show everything.
    /// Values are device-independent pixels; the presenter wants physical ones.
    /// </summary>
    /// <param name="widthDips">Needed width in device-independent pixels.</param>
    /// <param name="heightDips">Needed height in device-independent pixels.</param>
    public void SetMinimumSize(double widthDips, double heightDips)
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter)
        {
            return;
        }

        double scale = DisplayScale;
        int    width  = (int)Math.Ceiling(Math.Max(widthDips, AppSettings.MinimumWidth) * scale);
        int    height = (int)Math.Ceiling(heightDips * scale);
        presenter.PreferredMinimumWidth  = width;
        presenter.PreferredMinimumHeight = height;

        // Grow now if the window is under the new minimum. Windows applies a minimum only on the next resize, so
        // leaving it would let the window sit too small until the user happened to drag an edge, then jump.
        if ((AppWindow.Size.Width < width) || (AppWindow.Size.Height < height))
        {
            Resize(Math.Max(AppWindow.Size.Width, width), Math.Max(AppWindow.Size.Height, height));
        }
    }

    /// <summary>
    /// Brings an element up from nothing, so a page that replaces a dialog does not snap into place. Driven
    /// straight from the compositor: a XAML storyboard over the same property jumped to the end instead of
    /// running.
    /// </summary>
    /// <param name="element">What to fade in.</param>
    private static void FadeIn(UIElement element)
    {
        Microsoft.UI.Composition.Visual visual = ElementCompositionPreview.GetElementVisual(element);
        Microsoft.UI.Composition.Compositor compositor = visual.Compositor;

        Microsoft.UI.Composition.ScalarKeyFrameAnimation animation = compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0, 0);
        animation.InsertKeyFrame(
            1,
            1,
            compositor.CreateCubicBezierEasingFunction(new System.Numerics.Vector2(0.25f, 0.1f), new System.Numerics.Vector2(0.25f, 1f)));
        animation.Duration = TimeSpan.FromMilliseconds(FadeMilliseconds);

        visual.Opacity = 0;
        visual.StartAnimation("Opacity", animation);
    }

    /// <summary>Sizes the window in physical pixels, never larger than the screen it is on.</summary>
    /// <param name="width">Wanted width in physical pixels.</param>
    /// <param name="height">Wanted height in physical pixels.</param>
    private void Resize(int width, int height)
    {
        Windows.Graphics.RectInt32 work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(
            Math.Min(width, work.Width),
            Math.Min(height, work.Height)));
    }

    /// <summary>Remembers the restored size and whether the window was maximized, for the next launch.</summary>
    private void SaveWindowSize()
    {
        bool maximized = (AppWindow.Presenter as OverlappedPresenter)?.State == OverlappedPresenterState.Maximized;

        // A maximized window reports the screen size, so keep the last restored size in that case.
        App.Settings.IsMaximized = maximized;
        if (!maximized)
        {
            double scale        = DisplayScale;
            App.Settings.Width  = (int)Math.Round(AppWindow.Size.Width / scale);
            App.Settings.Height = (int)Math.Round(AppWindow.Size.Height / scale);
        }

        App.Settings.Save(AppSettings.DefaultPath);
    }

    /// <summary>
    /// Shows the settings dialog: the default fit mode for opened documents and the Windows default-app link.
    /// Changes save as they are made; the dialog only has a Done button.
    /// </summary>
    public async Task ShowSettingsAsync()
    {
        var fitModes = new ComboBox
        {
            ItemsSource   = new[] { "Fit page", "Fit width", "Actual size" },
            SelectedIndex = App.Settings.DefaultFitMode switch { ZoomMode.FitWidth => 1, ZoomMode.Custom => 2, _ => 0 },
            MinWidth      = 160,
        };
        fitModes.SelectionChanged += (_, _) =>
        {
            App.Settings.DefaultFitMode = fitModes.SelectedIndex switch { 1 => ZoomMode.FitWidth, 2 => ZoomMode.Custom, _ => ZoomMode.FitPage };
            App.Settings.Save(AppSettings.DefaultPath);
        };

        var defaultApp = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing     = 8,
                Children    =
                {
                    new FontIcon { Glyph = "\uE8A7", FontSize = 16 },
                    new TextBlock { Text = "Open Window Settings" },
                },
            },
        };
        defaultApp.Click += async (_, _) =>
        {
            try
            {
                Uri settings = App.IsPackaged
                    ? DefaultAppRegistration.SettingsUriFor(App.ApplicationUserModelId)
                    : DefaultAppRegistration.SettingsUri;
                await Windows.System.Launcher.LaunchUriAsync(settings);
            }
            catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
            {
                App.Log($"Could not open Default apps settings: {ex.Message}");
            }
        };

        var dialog = new ContentDialog
        {
            XamlRoot        = Content.XamlRoot,
            Title           = "Settings",
            CloseButtonText = "Done",
            DefaultButton   = ContentDialogButton.Close,
            Content         = new StackPanel
            {
                Spacing  = 16,
                MinWidth = 360,
                Children =
                {
                    SettingRow("Default zoom mode", "choose how a PDF is loaded  shown when it is open.", fitModes),
                    SettingRow("Make Default", "Make Aiko the app that opens PDFs.", defaultApp),
                },
            },
        };
        await dialog.ShowAsync();
    }

    private static StackPanel SettingRow(string title, string description, FrameworkElement control)
        => new()
        {
            Spacing  = 6,
            Children =
            {
                new TextBlock { Text = title, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] },
                new TextBlock
                {
                    Text         = description,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground   = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                },
                control,
            },
        };

    /// <summary>Closes any open document and shows the home page.</summary>
    public void ShowHome()
    {
        DetachViewer();
        RootFrame.Navigate(typeof(HomePage));
        RootFrame.BackStack.Clear();
        Title = AppName;

        current?.Dispose();
        current = null;
    }

    /// <summary>
    /// Shows the viewer's controls in the title bar: the pane toggle hides the sidebar and the open buttons appear
    /// on the right. Called by the viewer once it has been navigated to.
    /// </summary>
    /// <param name="page">The viewer being shown.</param>
    public void AttachViewer(ViewerPage page)
    {
        viewer                                = page;
        AppTitleBar.IsPaneToggleButtonVisible = true;
        OpenButton.Visibility                 = Visibility.Visible;
        OpenWithButton.Visibility             = Visibility.Visible;
        ShowInFolderButton.Visibility         = Visibility.Visible;
        PrintButton.Visibility                = Visibility.Visible;
    }

    private void DetachViewer()
    {
        viewer                                = null;
        DocumentTitle.Text                    = string.Empty;
        AppTitleBar.IsPaneToggleButtonVisible = false;
        OpenButton.Visibility                 = Visibility.Collapsed;
        OpenWithButton.Visibility             = Visibility.Collapsed;
        ShowInFolderButton.Visibility         = Visibility.Collapsed;
        PrintButton.Visibility                = Visibility.Collapsed;
    }

    /// <summary>Lets the user pick a PDF, then opens it.</summary>
    public async Task PickAndOpenAsync()
    {
        var picker = new FileOpenPicker
        {
            ViewMode               = PickerViewMode.List,
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeFilter.Add(".pdf");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

        StorageFile? file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            await OpenFileAsync(file.Path);
        }
    }

    /// <summary>
    /// Opens a PDF and shows it in the viewer. Asks for a password when the file needs one, and explains any
    /// failure in a dialog instead of throwing. A second open while one is in progress is ignored.
    /// </summary>
    /// <param name="path">Full path of the file.</param>
    public async Task OpenFileAsync(string path)
    {
        if (opening)
        {
            return;
        }

        opening = true;
        try
        {
            PdfSession? session = await OpenWithPasswordPromptAsync(path);
            if (session is null)
            {
                return;
            }

            DetachViewer();
            current?.Dispose();
            current = session;
            App.Recent.Add(path);

            RootFrame.Navigate(typeof(ViewerPage), session);
            RootFrame.BackStack.Clear();
            Title              = $"{session.Name} - {AppName}";
            DocumentTitle.Text = session.Name;

            _ = SaveThumbnailAsync(session, path);
        }
        catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
        {
            // Callers fire and forget, so a failure here would otherwise vanish with the task.
            App.Log($"Failed to show {path}: {ex}");
            await ShowErrorAsync(
                "Can't show this file",
                $"Something went wrong while opening \"{Path.GetFileName(path)}\".",
                ex.Message);
        }
        finally
        {
            opening = false;
        }
    }

    private async Task SaveThumbnailAsync(PdfSession session, string path)
    {
        try
        {
            await ThumbnailCache.SaveAsync(session.Renderer, path, Content.XamlRoot?.RasterizationScale ?? 1);

            // The list holds twenty files; previews for anything that fell off it are dead weight.
            ThumbnailCache.Prune(App.Recent.Items.Select(file => file.Path));
        }
        catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
        {
            // The home page falls back to an icon; not worth interrupting reading over.
            App.Log($"Thumbnail for {path} failed: {ex.Message}");
        }
    }

    private async Task<PdfSession?> OpenWithPasswordPromptAsync(string path)
    {
        string  name     = Path.GetFileName(path);
        string? password = null;
        while (true)
        {
            try
            {
                return await PdfSession.OpenAsync(path, password);
            }
            catch (PdfDocumentEncryptedException)
            {
                password = await AskPasswordAsync(name, wrongPassword: password is not null);
                if (password is null)
                {
                    return null;
                }
            }
            catch (FileNotFoundException)
            {
                App.Recent.Remove(path);
                await ShowErrorAsync("File not found", $"We can't find \"{name}\". It may have been moved or deleted.");
                return null;
            }
            catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
            {
                App.Log($"Failed to open {path}: {ex}");
                await ShowErrorAsync("Can't open this file", $"\"{name}\" can't be opened. It may be damaged.");
                return null;
            }
        }
    }

    private async Task<string?> AskPasswordAsync(string name, bool wrongPassword)
    {
        var box = new PasswordBox { PlaceholderText = "Password" };
        var dialog = new ContentDialog
        {
            XamlRoot          = Content.XamlRoot,
            Title             = "This PDF is protected",
            Content           = new StackPanel
            {
                Spacing  = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text         = wrongPassword ? $"That password didn't work. Try again for \"{name}\"." : $"Enter the password to open \"{name}\".",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    box,
                },
            },
            PrimaryButtonText = "Open",
            CloseButtonText   = "Cancel",
            DefaultButton     = ContentDialogButton.Primary,
        };

        ContentDialogResult result = await dialog.ShowAsync();
        return (result == ContentDialogResult.Primary) ? box.Password : null;
    }

    /// <summary>Shows a dialog explaining a failure in plain words, with an OK the reader can press blind.</summary>
    /// <param name="title">What went wrong, in a few words.</param>
    /// <param name="message">The explanation, naming the file.</param>
    /// <param name="detail">What the failure itself said, shown under the message. Omitted when there is none.</param>
    private async Task ShowErrorAsync(string title, string message, string? detail = null)
    {
        var lines = new StackPanel { Spacing = 8 };
        lines.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(detail))
        {
            lines.Children.Add(new TextBlock
            {
                Text         = detail,
                TextWrapping = TextWrapping.Wrap,
                Foreground   = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
        }

        var dialog = new ContentDialog
        {
            XamlRoot          = Content.XamlRoot,
            Title             = title,
            Content           = lines,
            PrimaryButtonText = "OK",
            DefaultButton     = ContentDialogButton.Primary,
        };
        await dialog.ShowAsync();
    }

    /// <summary>
    /// How many physical pixels the display puts in a device-independent one. Taken from the window itself rather
    /// than from XAML, which has no answer until the content has been measured: the window is sized before that,
    /// and falling back to 1 there opened the first window at four fifths of its size on a scaled display.
    /// </summary>
    private double DisplayScale
    {
        get
        {
            uint dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
            return (dpi > 0) ? (dpi / 96.0) : (Content?.XamlRoot?.RasterizationScale ?? 1);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hWnd);

    /// <summary>Keeps the button row clear of the caption buttons, whose width the frame reports in physical pixels.</summary>
    private void SizeCaptionSpacer()
    {
        double scale = DisplayScale;
        CaptionSpacer.Width = AppWindow.TitleBar.RightInset / scale;

        // The title is centered across the whole window, so it has to stay clear of the buttons on both sides or
        // a long file name runs underneath them.
        double clear = TitleButtons.ActualWidth + CaptionSpacer.Width + 16;
        DocumentTitle.MaxWidth = Math.Max(120, (AppWindow.Size.Width / scale) - (2 * clear));
    }

    // =========================================================================
    // ACTIVATION: DIM THE TITLE BAR IN STEP WITH THE CAPTION BUTTONS
    // =========================================================================

    private const uint WmNcActivate = 0x0086;

    /// <summary>
    /// Dims the title and buttons when the window loses focus and restores them when it regains it, from the same
    /// message that repaints the caption buttons. XAML's own Activated event arrives some 60ms later. A frame of
    /// difference can remain (XAML draws on its next frame; the caption repaints at once), as it does in the
    /// built-in Windows apps.
    /// </summary>
    private void HookActivation()
    {
        activationHook = OnWindowMessage;
        SetWindowSubclass(WinRT.Interop.WindowNative.GetWindowHandle(this), activationHook, 1, nint.Zero);
    }

    private nint OnWindowMessage(nint hWnd, uint msg, nint wParam, nint lParam, nint id, nint data)
    {
        if (msg == WmNcActivate)
        {
            bool   active  = wParam != nint.Zero;
            double opacity = active ? 1 : (double)Application.Current.Resources["TitleBarDeactivatedOpacity"];
            DocumentTitle.Opacity = opacity;
            TitleButtons.Opacity  = opacity;
        }

        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private delegate nint SubclassProc(nint hWnd, uint msg, nint wParam, nint lParam, nint id, nint data);

    [System.Runtime.InteropServices.DllImport("comctl32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint hWnd, SubclassProc proc, nint id, nint data);

    [System.Runtime.InteropServices.DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hWnd, uint msg, nint wParam, nint lParam);

    // =========================================================================
    // INPUT: TITLE BAR, SHORTCUT AND DRAG-DROP
    // =========================================================================

    private void OnPaneToggleRequested(TitleBar sender, object args) => viewer?.ToggleSidebar();

    private void OnOpenClick(object sender, RoutedEventArgs e) => _ = PickAndOpenAsync();

    private void OnShowInFolderClick(object sender, RoutedEventArgs e)
    {
        if (current is not null)
        {
            Shell.ShowInFolder(current.Path);
        }
    }

    private void OnPrintClick(object sender, RoutedEventArgs e) => _ = PrintAsync();

    private void OnPrintAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _ = PrintAsync();
    }

    /// <summary>Shows the Windows print dialog for the open document. Ignored while no document is open or a dialog is already up.</summary>
    private async Task PrintAsync()
    {
        if ((current is null) || printing)
        {
            return;
        }

        printing = true;
        try
        {
            await DocumentPrinter.PrintAsync(this, current);
        }
        catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
        {
            App.Log($"Print failed for {current.Path}: {ex}");
            await ShowErrorAsync("Can't print", "Printing isn't available right now.");
        }
        finally
        {
            printing = false;
        }
    }

    /// <summary>Shows the Windows "Open with" picker for the open document.</summary>
    private async void OnOpenWithClick(object sender, RoutedEventArgs e)
    {
        if (current is null)
        {
            return;
        }

        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(current.Path);
            await Windows.System.Launcher.LaunchFileAsync(file, new Windows.System.LauncherOptions { DisplayApplicationPicker = true });
        }
        catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
        {
            App.Log($"Open with failed for {current.Path}: {ex.Message}");
        }
    }

    /// <summary>
    /// The caption buttons live in their own child window (class ReunionWindowingCaptionControls) and keep their
    /// hover look when the pointer slides straight from them onto our controls, which sit in a sibling window, so
    /// they never see the pointer leave. Telling that window directly clears the highlight.
    /// TODO: remove once Windows App SDK clears caption hover on its own (seen in 2.4.0).
    /// </summary>
    private void OnRightHeaderPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        const uint mouseMove  = 0x0200;
        const uint mouseLeave = 0x02A3;

        nint captionControls = FindWindowEx(WinRT.Interop.WindowNative.GetWindowHandle(this), nint.Zero, "ReunionWindowingCaptionControls", null);
        if (captionControls != nint.Zero)
        {
            // A move to (-1,-1) lands on no button, the leave clears the highlight, and cancelling hover tracking
            // stops the tooltip that would otherwise still pop up for the button the pointer just left.
            SendMessage(captionControls, mouseMove, nint.Zero, -1);
            SendMessage(captionControls, mouseLeave, nint.Zero, nint.Zero);

            var cancelHover = new TrackMouseEventData
            {
                Size      = (uint)System.Runtime.InteropServices.Marshal.SizeOf<TrackMouseEventData>(),
                Flags     = 0x80000001,
                Window    = captionControls,
                HoverTime = 0,
            };
            TrackMouseEvent(ref cancelHover);
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct TrackMouseEventData
    {
        public uint Size;
        public uint Flags;
        public nint Window;
        public uint HoverTime;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool TrackMouseEvent(ref TrackMouseEventData data);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern nint FindWindowEx(nint parent, nint after, string className, string? windowName);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint SendMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    private void OnOpenAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _ = PickAndOpenAsync();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems) ? DataPackageOperation.Copy : DataPackageOperation.None;
        e.DragUIOverride.Caption = "Open";
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        try
        {
            IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();
            string? pdf = items.OfType<StorageFile>().Select(f => f.Path)
                .FirstOrDefault(p => p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
            if (pdf is not null)
            {
                await OpenFileAsync(pdf);
            }
        }
        catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
        {
            App.Log($"Drop failed: {ex}");
        }
    }
}
