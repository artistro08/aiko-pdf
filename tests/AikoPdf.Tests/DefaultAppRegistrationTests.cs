using AikoPdf.Services;
using Microsoft.Win32;
using Xunit;

namespace AikoPdf.Tests;

public sealed class DefaultAppRegistrationTests : IDisposable
{
    private readonly string      scratch = $@"Software\AikoPdfTests\{Guid.NewGuid():N}";
    private readonly RegistryKey root;

    public DefaultAppRegistrationTests()
    {
        root = Registry.CurrentUser.CreateSubKey(scratch);
    }

    public void Dispose()
    {
        root.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(scratch, throwOnMissingSubKey: false);
    }

    [Fact]
    public void Register_WritesProgIdOpenWithAndDefaultProgramsEntries()
    {
        const string exe = @"C:\Apps\Aiko\AikoPdf.exe";

        DefaultAppRegistration.Register(root, exe);

        Assert.Equal($"\"{exe}\" \"%1\"", root.OpenSubKey($@"Software\Classes\{DefaultAppRegistration.ProgId}\shell\open\command")!.GetValue(string.Empty));
        Assert.Equal(string.Empty, root.OpenSubKey(@"Software\Classes\.pdf\OpenWithProgids")!.GetValue(DefaultAppRegistration.ProgId));
        Assert.Equal(DefaultAppRegistration.ProgId, root.OpenSubKey($@"Software\{DefaultAppRegistration.AppName}\Capabilities\FileAssociations")!.GetValue(".pdf"));
        Assert.Equal($@"Software\{DefaultAppRegistration.AppName}\Capabilities", root.OpenSubKey(@"Software\RegisteredApplications")!.GetValue(DefaultAppRegistration.AppName));
        Assert.True(DefaultAppRegistration.IsRegistered(root, exe));
    }

    [Fact]
    public void Register_GivesPdfsTheFileIconNotTheAppIcon()
    {
        const string exe = @"C:\Apps\Aiko\AikoPdf.exe";

        DefaultAppRegistration.Register(root, exe);

        Assert.Equal(
            "\"C:\\Apps\\Aiko\\Assets\\AikoFile.ico\",0",
            root.OpenSubKey($@"Software\Classes\{DefaultAppRegistration.ProgId}\DefaultIcon")!.GetValue(string.Empty));
    }

    [Fact]
    public void IsRegistered_IsFalseWhileAnOlderRegistrationStillShowsTheAppIcon()
    {
        const string exe = @"C:\Apps\Aiko\AikoPdf.exe";
        DefaultAppRegistration.Register(root, exe);
        using (RegistryKey icon = root.CreateSubKey($@"Software\Classes\{DefaultAppRegistration.ProgId}\DefaultIcon"))
        {
            icon.SetValue(string.Empty, $"\"{exe}\",0");
        }

        Assert.False(DefaultAppRegistration.IsRegistered(root, exe));
    }

    [Fact]
    public void Register_IsIdempotentAndFollowsAMovedExecutable()
    {
        DefaultAppRegistration.Register(root, @"C:\Old\AikoPdf.exe");
        DefaultAppRegistration.Register(root, @"C:\New\AikoPdf.exe");

        Assert.False(DefaultAppRegistration.IsRegistered(root, @"C:\Old\AikoPdf.exe"));
        Assert.True(DefaultAppRegistration.IsRegistered(root, @"C:\New\AikoPdf.exe"));
    }

    [Fact]
    public void SettingsUriFor_PointsAtThePackagedApp()
    {
        // The "!" is escaped: Settings ignores the app and shows the whole list when it is not.
        Assert.Equal(
            "ms-settings:defaultapps?registeredAUMID=Artistro08.Aiko_abc123%21Aiko",
            DefaultAppRegistration.SettingsUriFor("Artistro08.Aiko_abc123!Aiko").OriginalString);
    }

    [Fact]
    public void IsRegistered_IsFalseBeforeRegistering()
    {
        Assert.False(DefaultAppRegistration.IsRegistered(root, @"C:\Apps\Aiko\AikoPdf.exe"));
    }
}
