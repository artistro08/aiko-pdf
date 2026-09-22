using System.Security.Cryptography;
using System.Text;
using AikoPdf.Services;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using IFileActivatedEventArgs = Windows.ApplicationModel.Activation.IFileActivatedEventArgs;
using ILaunchActivatedEventArgs = Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs;

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

    // Keys windows register under so a PDF opened elsewhere finds the right one: a window on its home page as
    // "home-" and its process id, a window showing a document as "doc-" and a hash of the file's path, and a window
    // busy opening a file as "busy-" and its process id, which no launch looks for.
    private const string HomeKeyPrefix     = "home-";
    private const string DocumentKeyPrefix = "doc-";
    private const string BusyKeyPrefix     = "busy-";

    // One marker per running window process, removed when its window closes. A marker left behind by a process
    // that is gone means that session died without closing: usually a native crash that no managed handler sees.
    private static readonly string SessionMarker = Path.Combine(
        Path.GetDirectoryName(LogPath)!, $"running-{Environment.ProcessId}");

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

    /// <summary>
    /// How Windows names this app when it is packaged: the package family name and the application id from the
    /// manifest. Empty for the portable build, which Windows knows by its registry entry instead.
    /// </summary>
    public static string ApplicationUserModelId
        => IsPackaged ? $"{Windows.ApplicationModel.Package.Current.Id.FamilyName}!Aiko" : string.Empty;

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

        // Failures off the UI thread: a background thread's crash, and a task whose error nobody awaited.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log($"Unhandled background exception (terminating: {e.IsTerminating}){Environment.NewLine}{e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log($"Unobserved task exception{Environment.NewLine}{e.Exception}");
            e.SetObserved();
        };

        StartSession();
    }

    /// <summary>The folder the crash log is written to (inside the package's own storage when packaged).</summary>
    public static string LogFolder => Path.GetDirectoryName(LogPath)!;

    /// <summary>Removes this process's running marker; called when the window closes normally.</summary>
    public static void EndSession()
    {
        try
        {
            File.Delete(SessionMarker);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stale marker only costs one false "ended unexpectedly" line.
        }
    }

    /// <summary>
    /// Logs any earlier session that ended without closing its window, then marks this one as running. Crashes
    /// inside XAML or the PDF engine kill the process before any handler runs, so this is the only trace they leave.
    /// </summary>
    private static void StartSession()
    {
        try
        {
            Directory.CreateDirectory(LogFolder);
            foreach (string marker in Directory.GetFiles(LogFolder, "running-*"))
            {
                if (!int.TryParse(Path.GetFileName(marker)["running-".Length..], out int pid) || IsAlive(pid))
                {
                    continue;
                }

                Log($"A previous session ({File.GetCreationTime(marker):O}) ended without closing: a crash, or the "
                    + "app was force-closed. For a crash, Event Viewer > Windows Logs > Application names the faulting module.");
                File.Delete(marker);
            }

            File.WriteAllText(SessionMarker, string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging must never stop the app from starting.
        }
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
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

    /// <summary>
    /// Opens the main window, and the PDF passed on the command line when there is one. A PDF already open in another
    /// window brings that window forward; otherwise one sitting on its home page takes it. Either way this launch
    /// ends without showing a window.
    /// </summary>
    /// <param name="args">Launch details (unused; the command line is read from the environment).</param>
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Worked out before the window is built, so a document opened from Explorer goes straight into the
        // viewer instead of flashing the home page on its way there.
        string? startupFile = StartupFile();
        if ((startupFile is not null) && await HandOffAsync(startupFile))
        {
            EndSession();
            Exit();
            return;
        }

        AppInstance.GetCurrent().Activated += OnHandedOff;
        Window = new MainWindow(startupFile);
        Window.Activate();

        // Keep Windows pointed at this executable so "Open with" and Default apps list it, wherever it lives.
        // Written only when it has moved, so a normal launch leaves the registry alone. The packaged build gets
        // the same association from its manifest instead.
        // The packaged build takes its association from the manifest, and its registry writes go into the package
        // anyway, so only the portable build registers itself here. tools\install.ps1 clears a portable entry left
        // behind when the package is installed.
        if (!IsPackaged)
        {
            try
            {
                string executable = Environment.ProcessPath ?? string.Empty;
                if (!DefaultAppRegistration.IsRegistered(Microsoft.Win32.Registry.CurrentUser, executable))
                {
                    DefaultAppRegistration.Register(Microsoft.Win32.Registry.CurrentUser, executable);

                    // Explorer caches file icons; this tells it the association changed so PDFs pick up the file
                    // icon now rather than after a restart.
                    NativeMethods.SHChangeNotify(NativeMethods.ShcneAssocChanged, NativeMethods.ShcnfIdList, nint.Zero, nint.Zero);
                }
            }
            catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
            {
                Log($"Default app registration failed: {ex.Message}");
            }
        }

    }

    // =========================================================================
    // SHARING PDFS BETWEEN WINDOWS
    // =========================================================================

    /// <summary>The key a window on its home page registers under: one per window, so two never compete for it.</summary>
    public static string HomeKey => HomeKeyPrefix + Environment.ProcessId;

    /// <summary>The key a window showing a document registers under, the same for every spelling of its path.</summary>
    /// <param name="path">Path of the open PDF.</param>
    /// <returns>"doc-" and a hash of the file's canonical path.</returns>
    public static string DocumentKey(string path)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Shell.CanonicalPath(path).ToUpperInvariant()));
        return DocumentKeyPrefix + Convert.ToHexString(hash)[..32];
    }

    /// <summary>
    /// Tells other launches what this window can take: <see cref="HomeKey"/> while it shows the home page, the
    /// <see cref="DocumentKey"/> of the PDF it shows, or nothing (null) while a file is opening.
    /// </summary>
    /// <param name="key">The key to register under, or null for none.</param>
    public static void SetWindowKey(string? key)
    {
        // Registering a new key replaces the old one. UnregisterKey is never used: after it, Windows App SDK 2.4
        // reports later registrations as done but other windows see no key at all, so "none" is a busy key instead.
        string wanted = key ?? (BusyKeyPrefix + Environment.ProcessId);
        try
        {
            if (AppInstance.GetCurrent().Key != wanted)
            {
                AppInstance.FindOrRegisterForKey(wanted);
            }
        }
        catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
        {
            // Without the key this window simply isn't offered; PDFs open in their own windows as before.
            Log($"Updating the window registration failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Brings forward another window that already shows a PDF, for an open from inside this window (the recent
    /// list, the picker, a drop), so the same file never ends up in two windows.
    /// </summary>
    /// <param name="path">The PDF about to be opened.</param>
    /// <returns>True when another window has it and was brought forward.</returns>
    public static bool ShowWindowWithDocument(string path)
    {
        if (FindWindow(DocumentKey(path)) is not { } other)
        {
            return false;
        }

        NativeMethods.BringProcessWindowToFront(other.ProcessId);
        return true;
    }

    /// <summary>
    /// Passes this launch to the window that should take it: the one already showing this PDF, or else one on its
    /// home page. That window opens it (or just comes forward) and this launch ends without a window of its own.
    /// </summary>
    /// <param name="path">The PDF this launch opens.</param>
    /// <returns>True when another window took the launch and this one should end.</returns>
    private static async Task<bool> HandOffAsync(string path)
    {
        try
        {
            AppInstance? target = FindWindow(DocumentKey(path)) ?? FindWindow(HomeKeyPrefix, prefix: true);
            if (target is null)
            {
                return false;
            }

            // This launch owns the foreground (the user just opened a file); it lends it to the window it hands to,
            // which Windows would otherwise only flash in the taskbar.
            NativeMethods.AllowSetForegroundWindow(target.ProcessId);
            await target.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());
            return true;
        }
        catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
        {
            // A window that closed a moment ago, or hung: open this PDF in a window of its own instead.
            Log($"Handing the PDF to an open window failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Another running window registered under a key, or under any key starting with it.</summary>
    private static AppInstance? FindWindow(string key, bool prefix = false)
        => AppInstance.GetInstances().FirstOrDefault(instance => !instance.IsCurrent
            && (prefix ? instance.Key.StartsWith(key, StringComparison.Ordinal) : (instance.Key == key)));

    /// <summary>A launch handed over by another copy of the app: opens its PDF here, on the UI thread.</summary>
    private static void OnHandedOff(object? sender, AppActivationArguments activation)
    {
        if (FileFrom(activation) is not { } path)
        {
            return;
        }

        // The window registers its key while it is still being built, a moment before Window is set; a launch
        // that lands in that gap gets a window of its own rather than being lost.
        if (Window is null)
        {
            OpenInNewWindow(path);
            return;
        }

        Window.DispatcherQueue.TryEnqueue(async () =>
        {
            // Two PDFs opened at once can both be sent here; the second finds this window already busy with the
            // first and goes to a new window instead of being dropped.
            if (!Window.CanTake(path))
            {
                OpenInNewWindow(path);
                return;
            }

            Window.BringToFront();
            await Window.OpenFileAsync(path);
        });
    }

    /// <summary>Starts another copy of the app for a PDF this window can't take.</summary>
    /// <param name="path">The PDF to open.</param>
    private static void OpenInNewWindow(string path)
    {
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            start.ArgumentList.Add(path);
            System.Diagnostics.Process.Start(start)?.Dispose();
        }
        catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
        {
            Log($"Opening {path} in a new window failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The PDF this launch should open: the one named on the command line, or the one Windows handed over when the
    /// packaged app was launched by double-clicking a file.
    /// </summary>
    /// <returns>A path to an existing file, or null.</returns>
    private static string? StartupFile()
    {
        if (LaunchArguments.FileFrom(Environment.GetCommandLineArgs().Skip(1)) is { } fromCommandLine)
        {
            return fromCommandLine;
        }

        try
        {
            return FileFrom(AppInstance.GetCurrent().GetActivatedEventArgs());
        }
        catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
        {
            Log($"Reading the activation arguments failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The PDF an activation names: a double-clicked file, or the command line of a launch (which is how a second
    /// copy of the app hands one over).
    /// </summary>
    /// <param name="activation">The activation to read.</param>
    /// <returns>A path to an existing file, or null.</returns>
    private static string? FileFrom(AppActivationArguments activation)
    {
        if ((activation.Kind == ExtendedActivationKind.File)
            && (activation.Data is IFileActivatedEventArgs files)
            && (files.Files.FirstOrDefault()?.Path is { } path)
            && File.Exists(path))
        {
            return path;
        }

        return ((activation.Kind == ExtendedActivationKind.Launch) && (activation.Data is ILaunchActivatedEventArgs launch))
            ? LaunchArguments.FileFrom(NativeMethods.SplitCommandLine(launch.Arguments))
            : null;
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
