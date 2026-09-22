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
    public void Register_IsIdempotentAndFollowsAMovedExecutable()
    {
        DefaultAppRegistration.Register(root, @"C:\Old\AikoPdf.exe");
        DefaultAppRegistration.Register(root, @"C:\New\AikoPdf.exe");

        Assert.False(DefaultAppRegistration.IsRegistered(root, @"C:\Old\AikoPdf.exe"));
        Assert.True(DefaultAppRegistration.IsRegistered(root, @"C:\New\AikoPdf.exe"));
    }

    [Fact]
    public void Unregister_TakesTheAppBackOutOfWindows()
    {
        DefaultAppRegistration.Register(root, @"C:\Apps\Aiko\AikoPdf.exe");
        DefaultAppRegistration.Unregister(root);

        Assert.Null(root.OpenSubKey($@"Software\Classes\{DefaultAppRegistration.ProgId}"));
        Assert.Null(root.OpenSubKey($@"Software\{DefaultAppRegistration.AppName}"));
        Assert.Null(root.OpenSubKey(@"Software\Classes\.pdf\OpenWithProgids")!.GetValue(DefaultAppRegistration.ProgId));
        Assert.Null(root.OpenSubKey(@"Software\RegisteredApplications")!.GetValue(DefaultAppRegistration.AppName));
        Assert.False(DefaultAppRegistration.IsRegistered(root, @"C:\Apps\Aiko\AikoPdf.exe"));
    }

    [Fact]
    public void Unregister_OnAFreshUserDoesNothing()
    {
        DefaultAppRegistration.Unregister(root);

        Assert.Null(root.OpenSubKey($@"Software\Classes\{DefaultAppRegistration.ProgId}"));
    }

    [Fact]
    public void SettingsUriFor_PointsAtThePackagedApp()
    {
        Assert.Equal(
            "ms-settings:defaultapps?registeredAUMID=Artistro08.Aiko_abc123!Aiko",
            DefaultAppRegistration.SettingsUriFor("Artistro08.Aiko_abc123!Aiko").ToString());
    }

    [Fact]
    public void IsRegistered_IsFalseBeforeRegistering()
    {
        Assert.False(DefaultAppRegistration.IsRegistered(root, @"C:\Apps\Aiko\AikoPdf.exe"));
    }
}
