using System.Runtime.InteropServices;

namespace AikoPdf.Pdf;

/// <summary>
/// The slice of PDFium's C API that the text layer uses: opening a document from memory, reading each character
/// and its box, the outline, and the page's drawn objects.
///
/// PDFium keeps global state and is not safe to call from two threads at once, across every document, so every
/// call from the app goes through <see cref="Gate"/>. The library is initialized once, the first time this type
/// is touched, and never torn down: the process exiting is its end.
/// </summary>
/// <remarks>
/// @author Devin Green (Artistro08)
/// @link https://pdfium.googlesource.com/pdfium/+/refs/heads/main/public/
/// </remarks>
internal static class Pdfium
{
    private const string Library = "pdfium";

    // =========================================================================
    // ERRORS AND OBJECT TYPES
    // =========================================================================

    /// <summary>The file is missing or can't be read.</summary>
    public const uint ErrorFile = 2;

    /// <summary>The file isn't a PDF, or is damaged.</summary>
    public const uint ErrorFormat = 3;

    /// <summary>A password is needed, or the one given is wrong.</summary>
    public const uint ErrorPassword = 4;

    /// <summary>The file uses a security handler PDFium doesn't support.</summary>
    public const uint ErrorSecurity = 5;

    /// <summary>A path object: lines and shapes, filled or stroked.</summary>
    public const int ObjectPath = 2;

    /// <summary>A form XObject: a group of objects drawn with its own transform.</summary>
    public const int ObjectForm = 5;

    /// <summary>A path that is not filled at all.</summary>
    public const int FillNone = 0;

    /// <summary>Serializes every call into the library, across all documents.</summary>
    public static readonly Lock Gate = new();

    static Pdfium()
    {
        FPDF_InitLibrary();
    }

    /// <summary>A 2D transform as PDFium stores it: x' = a·x + c·y + e, y' = b·x + d·y + f.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Matrix
    {
        /// <summary>Horizontal scale.</summary>
        public float A;

        /// <summary>Vertical shear.</summary>
        public float B;

        /// <summary>Horizontal shear.</summary>
        public float C;

        /// <summary>Vertical scale.</summary>
        public float D;

        /// <summary>Horizontal offset.</summary>
        public float E;

        /// <summary>Vertical offset.</summary>
        public float F;

        /// <summary>The transform that changes nothing.</summary>
        public static Matrix Identity => new() { A = 1, D = 1 };

        /// <summary>This transform applied after another one.</summary>
        /// <param name="inner">The transform applied first.</param>
        /// <returns>The combined transform.</returns>
        public readonly Matrix After(Matrix inner) => new()
        {
            A = (inner.A * A) + (inner.B * C),
            B = (inner.A * B) + (inner.B * D),
            C = (inner.C * A) + (inner.D * C),
            D = (inner.C * B) + (inner.D * D),
            E = (inner.E * A) + (inner.F * C) + E,
            F = (inner.E * B) + (inner.F * D) + F,
        };

        /// <summary>Maps a point through the transform.</summary>
        /// <param name="x">Horizontal position.</param>
        /// <param name="y">Vertical position.</param>
        /// <returns>The transformed point.</returns>
        public readonly (double X, double Y) Apply(double x, double y)
            => ((A * x) + (C * y) + E, (B * x) + (D * y) + F);
    }

    // =========================================================================
    // LIBRARY AND DOCUMENT
    // =========================================================================

    [DllImport(Library)]
    private static extern void FPDF_InitLibrary();

    [DllImport(Library)]
    public static extern uint FPDF_GetLastError();

    [DllImport(Library)]
    public static extern nint FPDF_LoadMemDocument64(nint data, nuint size, [MarshalAs(UnmanagedType.LPUTF8Str)] string? password);

    [DllImport(Library)]
    public static extern void FPDF_CloseDocument(nint document);

    [DllImport(Library)]
    public static extern int FPDF_GetPageCount(nint document);

    // =========================================================================
    // PAGES
    // =========================================================================

    [DllImport(Library)]
    public static extern nint FPDF_LoadPage(nint document, int pageIndex);

    [DllImport(Library)]
    public static extern void FPDF_ClosePage(nint page);

    [DllImport(Library)]
    public static extern float FPDF_GetPageWidthF(nint page);

    [DllImport(Library)]
    public static extern float FPDF_GetPageHeightF(nint page);

    [DllImport(Library)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FPDF_PageToDevice(
        nint page, int startX, int startY, int sizeX, int sizeY, int rotate,
        double pageX, double pageY, out int deviceX, out int deviceY);

    // =========================================================================
    // TEXT
    // =========================================================================

    [DllImport(Library)]
    public static extern nint FPDFText_LoadPage(nint page);

    [DllImport(Library)]
    public static extern void FPDFText_ClosePage(nint textPage);

    [DllImport(Library)]
    public static extern int FPDFText_CountChars(nint textPage);

    [DllImport(Library)]
    public static extern uint FPDFText_GetUnicode(nint textPage, int index);

    [DllImport(Library)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FPDFText_GetCharBox(
        nint textPage, int index, out double left, out double right, out double bottom, out double top);

    // =========================================================================
    // OUTLINE
    // =========================================================================

    [DllImport(Library)]
    public static extern nint FPDFBookmark_GetFirstChild(nint document, nint bookmark);

    [DllImport(Library)]
    public static extern nint FPDFBookmark_GetNextSibling(nint document, nint bookmark);

    [DllImport(Library)]
    public static extern uint FPDFBookmark_GetTitle(nint bookmark, nint buffer, uint length);

    [DllImport(Library)]
    public static extern nint FPDFBookmark_GetDest(nint document, nint bookmark);

    [DllImport(Library)]
    public static extern nint FPDFBookmark_GetAction(nint bookmark);

    [DllImport(Library)]
    public static extern nint FPDFAction_GetDest(nint document, nint action);

    [DllImport(Library)]
    public static extern int FPDFDest_GetDestPageIndex(nint document, nint dest);

    // =========================================================================
    // PAGE OBJECTS
    // =========================================================================

    [DllImport(Library)]
    public static extern int FPDFPage_CountObjects(nint page);

    [DllImport(Library)]
    public static extern nint FPDFPage_GetObject(nint page, int index);

    [DllImport(Library)]
    public static extern int FPDFPageObj_GetType(nint pageObject);

    [DllImport(Library)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FPDFPageObj_GetBounds(
        nint pageObject, out float left, out float bottom, out float right, out float top);

    [DllImport(Library)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FPDFPageObj_GetMatrix(nint pageObject, out Matrix matrix);

    [DllImport(Library)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FPDFPath_GetDrawMode(nint path, out int fillMode, [MarshalAs(UnmanagedType.Bool)] out bool stroke);

    [DllImport(Library)]
    public static extern int FPDFFormObj_CountObjects(nint formObject);

    [DllImport(Library)]
    public static extern nint FPDFFormObj_GetObject(nint formObject, uint index);

    /// <summary>Reads a bookmark's title, which PDFium hands back as UTF-16 with a terminating null.</summary>
    /// <param name="bookmark">The bookmark handle.</param>
    /// <returns>The title, or an empty string.</returns>
    public static string BookmarkTitle(nint bookmark)
    {
        // A title longer than this is damage, not a name; it is skipped rather than allocated.
        const uint MaxTitleBytes = 64 * 1024;

        uint bytes = FPDFBookmark_GetTitle(bookmark, nint.Zero, 0);
        if ((bytes <= 2) || (bytes > MaxTitleBytes))
        {
            return string.Empty;
        }

        nint buffer = Marshal.AllocHGlobal((int)bytes);
        try
        {
            FPDFBookmark_GetTitle(bookmark, buffer, bytes);
            return Marshal.PtrToStringUni(buffer) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
