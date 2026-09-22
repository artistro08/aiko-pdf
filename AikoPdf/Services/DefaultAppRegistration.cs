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
        => new($"ms-settings:defaultapps?registeredAUMID={applicationUserModelId}");

    /// <summary>Writes the registration under the given hive root.</summary>
    /// <param name="root">Normally <c>Registry.CurrentUser</c>; tests pass a throwaway key.</param>
    /// <param name="executablePath">Full path of the app executable.</param>
    public static void Register(RegistryKey root, string executablePath)
    {
        string command = $"\"{executablePath}\" \"%1\"";

        using (RegistryKey progId = root.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            progId.SetValue(string.Empty, "PDF Document");
            using (RegistryKey icon = progId.CreateSubKey("DefaultIcon"))
            {
                icon.SetValue(string.Empty, $"\"{executablePath}\",0");
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

    /// <summary>
    /// Removes the registration. The packaged build calls this so a folder copy the user ran earlier stops showing
    /// a second, dead Aiko under "Open with"; the package carries its own association.
    /// </summary>
    /// <param name="root">Normally <c>Registry.CurrentUser</c>; tests pass a throwaway key.</param>
    public static void Unregister(RegistryKey root)
    {
        root.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);
        root.DeleteSubKeyTree($@"Software\{AppName}", throwOnMissingSubKey: false);

        using (RegistryKey? openWith = root.OpenSubKey(@"Software\Classes\.pdf\OpenWithProgids", writable: true))
        {
            openWith?.DeleteValue(ProgId, throwOnMissingValue: false);
        }

        using RegistryKey? registered = root.OpenSubKey(@"Software\RegisteredApplications", writable: true);
        registered?.DeleteValue(AppName, throwOnMissingValue: false);
    }

    /// <summary>True when the registration points at the given executable.</summary>
    /// <param name="root">The hive root that was registered under.</param>
    /// <param name="executablePath">Full path of the app executable.</param>
    /// <returns>True when the open command already names this executable.</returns>
    public static bool IsRegistered(RegistryKey root, string executablePath)
    {
        using RegistryKey? command = root.OpenSubKey($@"Software\Classes\{ProgId}\shell\open\command");
        return command?.GetValue(string.Empty) is string value
            && value.StartsWith($"\"{executablePath}\"", StringComparison.OrdinalIgnoreCase);
    }
}
