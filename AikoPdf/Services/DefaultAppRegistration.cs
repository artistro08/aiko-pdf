using Microsoft.Win32;

namespace AikoPdf.Services;

/// <summary>
/// Makes Windows aware of the app as a PDF handler, so it shows up under "Open with" and in Settings &gt; Default
/// apps, where the user can pick it. Everything goes under the current user's hive, so no elevation is needed,
/// and it is re-run on every launch so a moved executable stays registered.
///
/// Windows does not let an app make itself the default; the user confirms that in Settings. <see cref="SettingsUri"/>
/// deep-links straight to the app's own page there.
/// </summary>
/// <remarks>
/// @author Devin Green (Artistro08)
/// @link https://learn.microsoft.com/en-us/windows/win32/shell/default-programs
/// </remarks>
public static class DefaultAppRegistration
{
    /// <summary>The name Windows lists the app under.</summary>
    public const string AppName = "Aiko";

    /// <summary>The ProgId that ties .pdf to the app.</summary>
    public const string ProgId = "Aiko.PDF";

    /// <summary>The Settings page where the user picks default apps, opened on the registered app's entry.</summary>
    public static readonly Uri SettingsUri = new($"ms-settings:defaultapps?registeredAppUser={AppName}");

    /// <summary>
    /// The Settings page opened on a packaged app's entry. A package is listed by its application user model id,
    /// not by the registry name the portable build writes, so the two builds need different links.
    /// </summary>
    /// <param name="applicationUserModelId">The package's AUMID, family name and application id.</param>
    /// <returns>A deep link into Settings &gt; Default apps.</returns>
    public static Uri SettingsUriFor(string applicationUserModelId)
    {
        // The id has to be escaped: Settings drops the app and shows the plain list when the "!" before the
        // application id arrives unencoded.
        return new Uri($"ms-settings:defaultapps?registeredAUMID={Uri.EscapeDataString(applicationUserModelId)}");
    }

    /// <summary>The icon PDFs show in Explorer while the portable build is their app, shipped beside the executable.</summary>
    /// <param name="executablePath">Full path of the app executable.</param>
    /// <returns>The .ico path in the executable's Assets folder.</returns>
    public static string FileIconPath(string executablePath)
        => Path.Combine(Path.GetDirectoryName(executablePath) ?? string.Empty, "Assets", "AikoFile.ico");

    /// <summary>Writes the registration under the given hive root.</summary>
    /// <param name="root">Normally <c>Registry.CurrentUser</c>; tests pass a throwaway key.</param>
    /// <param name="executablePath">Full path of the app executable.</param>
    public static void Register(RegistryKey root, string executablePath)
    {
        string command  = $"\"{executablePath}\" \"%1\"";
        string fileIcon = FileIconValue(executablePath);

        using (RegistryKey progId = root.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            progId.SetValue(string.Empty, "PDF Document");
            using (RegistryKey icon = progId.CreateSubKey("DefaultIcon"))
            {
                icon.SetValue(string.Empty, fileIcon);
            }

            using RegistryKey open = progId.CreateSubKey(@"shell\open\command");
            open.SetValue(string.Empty, command);
        }

        // Listed under "Open with" for .pdf even before it is the default.
        using (RegistryKey openWith = root.CreateSubKey(@"Software\Classes\.pdf\OpenWithProgids"))
        {
            openWith.SetValue(ProgId, string.Empty);
        }

        // Default Programs registration: what Settings shows and which types the app claims.
        using (RegistryKey capabilities = root.CreateSubKey($@"Software\{AppName}\Capabilities"))
        {
            capabilities.SetValue("ApplicationName", AppName);
            capabilities.SetValue("ApplicationDescription", "A simple PDF reader.");
            using RegistryKey associations = capabilities.CreateSubKey("FileAssociations");
            associations.SetValue(".pdf", ProgId);
        }

        using RegistryKey registered = root.CreateSubKey(@"Software\RegisteredApplications");
        registered.SetValue(AppName, $@"Software\{AppName}\Capabilities");
    }

    /// <summary>True when the registration points at the given executable and its file icon.</summary>
    /// <param name="root">The hive root that was registered under.</param>
    /// <param name="executablePath">Full path of the app executable.</param>
    /// <returns>
    /// True when the open command names this executable and PDFs use its file icon. An older registration that
    /// still shows the app icon on PDFs reads as not registered, so the next launch rewrites it.
    /// </returns>
    public static bool IsRegistered(RegistryKey root, string executablePath)
    {
        using RegistryKey? command = root.OpenSubKey($@"Software\Classes\{ProgId}\shell\open\command");
        using RegistryKey? icon    = root.OpenSubKey($@"Software\Classes\{ProgId}\DefaultIcon");
        return (command?.GetValue(string.Empty) is string value)
            && value.StartsWith($"\"{executablePath}\"", StringComparison.OrdinalIgnoreCase)
            && string.Equals(icon?.GetValue(string.Empty) as string, FileIconValue(executablePath), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The DefaultIcon value: the file icon's path, quoted, and the index of its one icon.</summary>
    private static string FileIconValue(string executablePath) => $"\"{FileIconPath(executablePath)}\",0";
}
