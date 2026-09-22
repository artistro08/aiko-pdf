using AikoPdf.Services;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using IFileActivatedEventArgs = Windows.ApplicationModel.Activation.IFileActivatedEventArgs;

namespace AikoPdf;

/// <summary>
/// Application entry point and shared app state: the recent files list, the main window and the crash log.
/// </summary>
/// <remarks>
/// @author Devin Green (Artistro08)
/// @link https://learn.microsoft.com/en-us/windows/apps/winui/winui3/
/// </remarks>
public partial class App : Application
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AikoPdf", "crash.log");

    private static readonly object LogGate = new();

    /// <summary>The list of recently opened PDFs shown on the home page.</summary>
    public static RecentFiles Recent { get; } = RecentFiles.Load(RecentFiles.DefaultStorePath);

    /// <summary>Window size and reading preferences, loaded once and saved whenever they change.</summary>
    public static AppSettings Settings { get; } = AppSettings.Load(AppSettings.DefaultPath);

    /// <summary>The main application window, set once the app has launched.</summary>
    public static MainWindow Window { get; private set; } = null!;

    /// <summary>
    /// True when the app is running from its MSIX package. A packaged app takes its PDF association from the
    /// package manifest and has its registry writes redirected into the package, so the registration the portable
    /// build makes is both unnecessary and invisible there.
    /// </summary>
    public static bool IsPackaged { get; } = HasPackageIdentity();

    private static bool HasPackageIdentity()
    {
        try
        {
            return Windows.ApplicationModel.Package.Current.Id is not null;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Wires the crash handler and loads the XAML resources.</summary>
    public App()
    {
        // Testing aid: "--light" or "--dark" forces a theme for one run instead of following Windows.
        string[] args = Environment.GetCommandLineArgs();
        if (args.Contains("--light", StringComparer.OrdinalIgnoreCase))
        {
            RequestedTheme = ApplicationTheme.Light;
        }
        else if (args.Contains("--dark", StringComparer.OrdinalIgnoreCase))
        {
            RequestedTheme = ApplicationTheme.Dark;
        }

        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    /// <summary>Appends a line to %LOCALAPPDATA%\AikoPdf\crash.log. Never throws.</summary>
    /// <param name="message">What happened.</param>
    public static void Log(string message)
    {
        // Renders, text extraction and the UI thread all report here, so the writes are serialized: two at once
        // would otherwise throw a sharing violation and lose both lines.
        lock (LogGate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nowhere left to report to.
            }
        }
    }

    /// <summary>Opens the main window, and the PDF passed on the command line when there is one.</summary>
    /// <param name="args">Launch details (unused; the command line is read from the environment).</param>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Window = new MainWindow();
        Window.Activate();

        // Keep Windows pointed at this executable so "Open with" and Default apps list it, wherever it lives.
        // Written only when it has moved, so a normal launch leaves the registry alone. The packaged build gets
        // the same association from its manifest instead.
        if (!IsPackaged)
        {
            try
            {
                string executable = Environment.ProcessPath ?? string.Empty;
                if (!DefaultAppRegistration.IsRegistered(Microsoft.Win32.Registry.CurrentUser, executable))
                {
                    DefaultAppRegistration.Register(Microsoft.Win32.Registry.CurrentUser, executable);
                }
            }
            catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
            {
                Log($"Default app registration failed: {ex.Message}");
            }
        }

        if (StartupFile() is { } startupFile)
        {
            _ = Window.OpenFileAsync(startupFile);
        }
    }

    /// <summary>
    /// The PDF this launch should open: the one named on the command line, or the one Windows handed over when the
    /// packaged app was launched by double-clicking a file.
    /// </summary>
    /// <returns>A path to an existing file, or null.</returns>
    private static string? StartupFile()
    {
        if (Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(File.Exists) is { } fromCommandLine)
        {
            return fromCommandLine;
        }

        try
        {
            AppActivationArguments activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            if ((activation.Kind == ExtendedActivationKind.File)
                && (activation.Data is IFileActivatedEventArgs files)
                && (files.Files.FirstOrDefault()?.Path is { } path)
                && File.Exists(path))
            {
                return path;
            }
        }
        catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
        {
            Log($"Reading the activation arguments failed: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Writes the failure to the crash log and keeps the app running, because one failed action must not take the
    /// whole app down. A failure the process cannot carry on from (memory exhausted, a corrupt runtime state) is
    /// left to crash: continuing past it would only write worse data to the user's settings and caches.
    /// </summary>
    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Log($"{e.Message}{Environment.NewLine}{e.Exception}");
        e.Handled = ExceptionFilters.IsRecoverable(e.Exception);
    }
}
