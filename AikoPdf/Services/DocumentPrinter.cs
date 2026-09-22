using AikoPdf.Pdf;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Printing;
using Windows.Graphics.Printing;
using Windows.Storage.Streams;

namespace AikoPdf.Services;

/// <summary>
/// Prints the open document through the Windows print dialog.
///
/// Pages are rendered to bitmaps one at a time, as the print system asks for them: the dialog's preview asks for
/// the page it is showing, and the spooler asks for the whole document once. Only a handful of preview bitmaps are
/// kept, so a 500-page file costs about as much memory as a 5-page one and the dialog opens straight away.
/// </summary>
/// <remarks>
/// @author Devin Green (Artistro08)
/// @link https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/print-from-your-app
/// </remarks>
public sealed class DocumentPrinter
{
    // Enough resolution for text on paper without making each page bitmap huge.
    private const double PrintDpi = 150;

    // How many preview pages to hold. The dialog shows one at a time and flips back and forth between a few.
    private const int PreviewCacheSize = 4;

    private readonly PdfSession           session;
    private readonly PrintManager         manager;
    private readonly PrintDocument        document = new();
    private readonly IPrintDocumentSource source;
    private readonly Dictionary<int, Image> preview = [];
    private readonly List<int>             previewOrder = [];

    private DocumentPrinter(PdfSession session, PrintManager manager)
    {
        this.session = session;
        this.manager = manager;
        source       = document.DocumentSource;

        document.Paginate       += (_, _) => document.SetPreviewPageCount(session.PageCount, PreviewPageCountType.Final);
        document.GetPreviewPage += (_, e) => _ = ShowPreviewPageAsync(e.PageNumber);
        document.AddPages       += (_, _) => _ = AddPagesAsync();
    }

    /// <summary>Shows the Windows print dialog for a document, rendering its pages as the dialog asks for them.</summary>
    /// <param name="window">The window the dialog belongs to.</param>
    /// <param name="session">The open document.</param>
    /// <exception cref="NotSupportedException">The device has no print support.</exception>
    public static async Task PrintAsync(Window window, PdfSession session)
    {
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        if (!PrintManager.IsSupported())
        {
            throw new NotSupportedException("Printing is not available on this device.");
        }

        if (!IsSpoolerRunning())
        {
            throw new PrintSpoolerStoppedException();
        }

        var printer = new DocumentPrinter(session, PrintManagerInterop.GetForWindow(hwnd));
        printer.manager.PrintTaskRequested += printer.OnPrintTaskRequested;
        try
        {
            await PrintManagerInterop.ShowPrintUIForWindowAsync(hwnd);
        }
        finally
        {
            printer.manager.PrintTaskRequested -= printer.OnPrintTaskRequested;
        }
    }

    /// <summary>
    /// True when the Windows print spooler is answering. With it stopped the print dialog still opens but lists
    /// no printers, not even Microsoft Print to PDF, and its preview never finishes loading.
    /// </summary>
    /// <returns>False only when the spooler is known to be down.</returns>
    public static bool IsSpoolerRunning()
    {
        // Asking for the local printer list with no buffer fails either way; why it fails is the answer. A
        // running spooler says the buffer is too small, a stopped one says the RPC server is unavailable.
        const uint EnumLocal         = 0x2;
        const int  ServerUnavailable = 1722;

        bool listed = EnumPrinters(EnumLocal, null, 1, nint.Zero, 0, out uint _, out uint _);
        return listed || (System.Runtime.InteropServices.Marshal.GetLastWin32Error() != ServerUnavailable);
    }

    [System.Runtime.InteropServices.DllImport("winspool.drv", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool EnumPrinters(uint flags, string? name, uint level, nint buffer, uint size, out uint needed, out uint returned);

    /// <summary>Renders one page as a print page, sized in device-independent pixels at 96 per inch.</summary>
    /// <param name="pageNumber">1-based page number.</param>
    /// <returns>An image the print system can lay onto paper.</returns>
    private async Task<Image> RenderPageAsync(int pageNumber)
    {
        Windows.Foundation.Size size  = session.PageSizes[pageNumber - 1];
        using IRandomAccessStream png = await session.Renderer.RenderAsync(pageNumber, size.Width / 72 * PrintDpi);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(png);

        return new Image
        {
            Source  = bitmap,
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
            Width   = size.Width / 72 * 96,
            Height  = size.Height / 72 * 96,
        };
    }

    private async Task ShowPreviewPageAsync(int pageNumber)
    {
        try
        {
            if (!preview.TryGetValue(pageNumber, out Image? page))
            {
                page = await RenderPageAsync(pageNumber);
                preview[pageNumber] = page;
                previewOrder.Add(pageNumber);

                // Oldest first, so flipping through a long document never accumulates bitmaps.
                while (previewOrder.Count > PreviewCacheSize)
                {
                    preview.Remove(previewOrder[0]);
                    previewOrder.RemoveAt(0);
                }
            }

            document.SetPreviewPage(pageNumber, page);
        }
        catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
        {
            App.Log($"Print preview of page {pageNumber} failed: {ex.Message}");
        }
    }

    private async Task AddPagesAsync()
    {
        try
        {
            for (int n = 1; n <= session.PageCount; n++)
            {
                // Rendered, handed over, and let go of: the print system keeps what it still needs.
                document.AddPage(await RenderPageAsync(n));
            }

            document.AddPagesComplete();
        }
        catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
        {
            App.Log($"Printing {session.Path} failed while rendering: {ex.Message}");

            // The dialog waits for this either way; ending the sequence lets it report a failure instead of hanging.
            document.AddPagesComplete();
        }
    }

    private void OnPrintTaskRequested(PrintManager sender, PrintTaskRequestedEventArgs args)
    {
        PrintTask task = args.Request.CreatePrintTask(session.Name, sourceArgs => sourceArgs.SetSource(source));
        task.Completed += (_, e) =>
        {
            if (e.Completion == PrintTaskCompletion.Failed)
            {
                App.Log($"Printing {session.Path} failed.");
            }
        };
    }
}

/// <summary>The Windows print spooler is stopped, so no printer can be reached.</summary>
/// <remarks>
/// @author Devin Green (Artistro08)
/// </remarks>
public sealed class PrintSpoolerStoppedException : Exception
{
    /// <summary>Creates the exception with its standard message.</summary>
    public PrintSpoolerStoppedException()
        : base("The Print Spooler service is not running.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What went wrong.</param>
    public PrintSpoolerStoppedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the failure behind it.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The underlying failure.</param>
    public PrintSpoolerStoppedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
