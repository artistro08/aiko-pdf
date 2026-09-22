namespace AikoPdf.Services;

/// <summary>
/// Finds the PDF a launch asked for in its command line. Explorer starts the app as <c>AikoPdf.exe "C:\file.pdf"</c>,
/// and a launch handed over from a second copy of the app arrives as that same command line in one string.
/// </summary>
/// <remarks>
/// @author Devin Green (Artistro08)
/// @link https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-commandlinetoargvw
/// </remarks>
public static class LaunchArguments
{
    /// <summary>The first argument that names an existing file other than the app itself.</summary>
    /// <param name="arguments">The split command line, with or without the executable first.</param>
    /// <returns>The file's path, or null when no argument names one.</returns>
    public static string? FileFrom(IEnumerable<string> arguments)
        => arguments.FirstOrDefault(argument => !string.IsNullOrWhiteSpace(argument)
            && !argument.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && File.Exists(argument));
}
