using System.Runtime.InteropServices;

// Native libraries load only from System32 and the app's own folder, never from the current directory or PATH,
// so a planted DLL beside an opened PDF can't be picked up in place of pdfium.dll or a Windows library.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32 | DllImportSearchPath.AssemblyDirectory)]

namespace AikoPdf;

/// <summary>
/// The Win32 calls the app makes where WinUI has no equivalent: window DPI and subclassing, the caption buttons'
/// hover state, double-click metrics, the print spooler check, long path names, the shell icon refresh, command-line
/// splitting and handing the foreground to another window.
/// PDFium's own API lives in <see cref="Pdf.Pdfium"/>.
/// </summary>
/// <remarks>
/// @author Devin Green (Artistro08)
/// @link https://learn.microsoft.com/en-us/dotnet/standard/native-interop/best-practices
/// </remarks>
internal static class NativeMethods
{
    /// <summary>A window subclass callback, as SetWindowSubclass calls it.</summary>
    internal delegate nint SubclassProc(nint hWnd, uint msg, nint wParam, nint lParam, nint id, nint data);

    /// <summary>The TRACKMOUSEEVENT structure.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct TrackMouseEventData
    {
        /// <summary>Size of the structure in bytes.</summary>
        public uint Size;

        /// <summary>TME_* flags.</summary>
        public uint Flags;

        /// <summary>The window to track.</summary>
        public nint Window;

        /// <summary>Hover time-out in milliseconds.</summary>
        public uint HoverTime;
    }

    // =========================================================================
    // USER32
    // =========================================================================

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(nint hWnd);

    [DllImport("user32.dll")]
    internal static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TrackMouseEvent(ref TrackMouseEventData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowExW")]
    internal static extern nint FindWindowEx(nint parent, nint after, string className, string? windowName);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    internal static extern nint SendMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    // =========================================================================
    // COMCTL32
    // =========================================================================

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowSubclass(nint hWnd, SubclassProc proc, nint id, nint data);

    [DllImport("comctl32.dll")]
    internal static extern nint DefSubclassProc(nint hWnd, uint msg, nint wParam, nint lParam);

    // =========================================================================
    // WINSPOOL AND KERNEL32
    // =========================================================================

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, EntryPoint = "EnumPrintersW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumPrinters(uint flags, string? name, uint level, nint buffer, uint size, out uint needed, out uint returned);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetLongPathNameW", SetLastError = true)]
    internal static extern uint GetLongPathName(string shortPath, [Out] char[]? longPath, uint length);

    // =========================================================================
    // SHELL32
    // =========================================================================

    /// <summary>SHCNE_ASSOCCHANGED: a file type association changed.</summary>
    internal const int ShcneAssocChanged = 0x08000000;

    /// <summary>SHCNF_IDLIST: the item arguments are ID lists (unused with SHCNE_ASSOCCHANGED).</summary>
    internal const uint ShcnfIdList = 0x0000;

    [DllImport("shell32.dll")]
    internal static extern void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "CommandLineToArgvW", SetLastError = true)]
    private static extern nint CommandLineToArgv(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);

    /// <summary>Splits a command line the way Windows splits one for a program's arguments, quotes and all.</summary>
    /// <param name="commandLine">The whole command line.</param>
    /// <returns>The arguments; empty when the line can't be split.</returns>
    internal static string[] SplitCommandLine(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return [];
        }

        nint argv = CommandLineToArgv(commandLine, out int count);
        if (argv == nint.Zero)
        {
            return [];
        }

        try
        {
            string[] arguments = new string[count];
            for (int i = 0; i < count; i++)
            {
                arguments[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * nint.Size)) ?? string.Empty;
            }

            return arguments;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    // =========================================================================
    // FOREGROUND
    // =========================================================================

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint hWnd);

    private delegate bool EnumWindowsProc(nint hWnd, nint data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint data);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    private static extern int GetClassName(nint hWnd, [Out] char[] className, int maxCount);

    /// <summary>
    /// Brings another Aiko process's window in front, restoring it first if it is minimized. Works while this
    /// process owns the foreground, which it does while it is answering the user's own click.
    /// </summary>
    /// <param name="processId">The other window's process.</param>
    internal static void BringProcessWindowToFront(uint processId)
    {
        const int    Restore     = 9;
        const string WindowClass = "WinUIDesktopWin32WindowClass";

        nint   found  = nint.Zero;
        char[] buffer = new char[64];
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out uint owner);
            int length = (owner == processId) && IsWindowVisible(hWnd) ? GetClassName(hWnd, buffer, buffer.Length) : 0;
            if ((length > 0) && new string(buffer, 0, length).Equals(WindowClass, StringComparison.Ordinal))
            {
                found = hWnd;
                return false;
            }

            return true;
        }, nint.Zero);

        if (found == nint.Zero)
        {
            return;
        }

        if (IsIconic(found))
        {
            ShowWindow(found, Restore);
        }

        SetForegroundWindow(found);
    }
}
