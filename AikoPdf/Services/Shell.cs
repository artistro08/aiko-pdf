using System.Diagnostics;

namespace AikoPdf.Services;

/// <summary>
/// Small hand-offs to Windows Explorer.
/// </summary>
/// <remarks>
/// @author Devin Green (Artistro08)
/// </remarks>
public static class Shell
{
    /// <summary>
    /// Opens the file's folder in Explorer with the file selected, the standard way (explorer.exe /select). The path
    /// is expanded to its long form first because /select ignores 8.3 short names. Falls back to the folder alone
    /// if the file is gone.
    /// </summary>
    /// <param name="path">Full path of the file.</param>
    public static void ShowInFolder(string path)
    {
        try
        {
            string full = CanonicalPath(path);
            if (File.Exists(full))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{full}\"") { UseShellExecute = true });
                return;
            }

            string? folder = Path.GetDirectoryName(full);
            if ((folder is not null) && Directory.Exists(folder))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
            }
        }
        catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
        {
            App.Log($"Show in folder failed for {path}: {ex.Message}");
        }
    }

    /// <summary>
    /// One spelling for a file's path: made absolute, with 8.3 short names expanded, so <c>C:\Users\ARTIST~1\a.pdf</c>
    /// and <c>C:\Users\artistro08\a.pdf</c> compare as the same file.
    /// </summary>
    /// <param name="path">Any path Windows accepts.</param>
    /// <returns>The full long path, or the path as given when it can't be expanded.</returns>
    public static string CanonicalPath(string path)
    {
        try
        {
            return LongPath(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    /// <summary>Expands 8.3 short names in a path. Paths longer than MAX_PATH work too: the buffer grows to fit.</summary>
    /// <param name="path">A full path.</param>
    /// <returns>The long form, or the path unchanged when Windows can't expand it.</returns>
    private static string LongPath(string path)
    {
        uint needed = NativeMethods.GetLongPathName(path, null, 0);
        if (needed == 0)
        {
            return path;
        }

        char[] buffer = new char[needed];
        uint   length = NativeMethods.GetLongPathName(path, buffer, needed);
        return ((length > 0) && (length < needed)) ? new string(buffer, 0, (int)length) : path;
    }
}
