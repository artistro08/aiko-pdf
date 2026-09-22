using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

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
            string full = LongPath(Path.GetFullPath(path));
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

    private static string LongPath(string path)
    {
        var buffer = new StringBuilder(1024);
        uint length = GetLongPathNameW(path, buffer, (uint)buffer.Capacity);
        return ((length > 0) && (length < buffer.Capacity)) ? buffer.ToString() : path;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetLongPathNameW(string shortPath, StringBuilder longPath, uint length);
}
