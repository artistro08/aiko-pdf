using System.Text.Json;
using System.Text.Json.Serialization;
using AikoPdf.Pdf;

namespace AikoPdf.Services;

/// <summary>
/// Everything the app remembers between runs: the window size and the reading preferences from the settings
/// dialog. Saved as JSON under %LOCALAPPDATA%\AikoPdf. The path is injectable so tests never touch the real file.
/// </summary>
/// <remarks>
/// @author Devin Green (Artistro08)
/// </remarks>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters    = { new JsonStringEnumConverter() },
    };

    /// <summary>Narrowest window the app allows, in device-independent pixels; the height comes from the home page's content.</summary>
    public const int MinimumWidth = 720;

    /// <summary>Size of the window the first time the app runs, in device-independent pixels.</summary>
    public const int DefaultWidth  = 720;

    /// <summary>Height of the window the first time the app runs, in device-independent pixels.</summary>
    public const int DefaultHeight = 648;

    /// <summary>Shortest window a saved size is trusted at, in device-independent pixels.</summary>
    public const int MinimumHeight = 520;

    /// <summary>
    /// Last restored (not maximized) width in device-independent pixels; 0 when never saved. Stored in the same
    /// unit as the minimums above, so a window moved between monitors of different scaling comes back the size it
    /// looked, not the number of pixels it happened to cover.
    /// </summary>
    public int Width { get; set; }

    /// <summary>Last restored (not maximized) height in device-independent pixels; 0 when never saved.</summary>
    public int Height { get; set; }

    /// <summary>True when the window was maximized at exit.</summary>
    public bool IsMaximized { get; set; }

    /// <summary>How a document is zoomed when it opens. <see cref="ZoomMode.Custom"/> means actual size (100%).</summary>
    public ZoomMode DefaultFitMode { get; set; } = ZoomMode.FitPage;

    /// <summary>True when a usable window size was saved.</summary>
    [JsonIgnore]
    public bool HasSize => (Width >= MinimumWidth) && (Height >= MinimumHeight);

    /// <summary>The default store location for this Windows user.</summary>
    public static string DefaultPath
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AikoPdf", "settings.json");

    /// <summary>Reads the saved settings. A missing or unreadable file gives defaults rather than a failed start.</summary>
    /// <param name="path">Where the JSON lives.</param>
    /// <returns>The saved settings, or defaults.</returns>
    public static AppSettings Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
        {
            // Loaded from a static initializer: a malformed file must never keep the app from starting.
            return new AppSettings();
        }
    }

    /// <summary>Writes the settings. Failures are swallowed: losing them is a nuisance, not an error worth showing.</summary>
    /// <param name="path">Where the JSON goes.</param>
    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing to do; next launch uses the defaults.
        }
    }
}
