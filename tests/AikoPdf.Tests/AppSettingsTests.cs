using AikoPdf.Pdf;
using AikoPdf.Services;
using Xunit;

namespace AikoPdf.Tests;

public sealed class AppSettingsTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), $"aikopdf-settings-{Guid.NewGuid():N}");

    private string StorePath => Path.Combine(folder, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Load_MissingFile_GivesDefaults()
    {
        AppSettings settings = AppSettings.Load(StorePath);

        Assert.False(settings.HasSize);
        Assert.False(settings.IsMaximized);
        Assert.Equal(ZoomMode.FitPage, settings.DefaultFitMode);
    }

    [Fact]
    public void Save_RoundTrips()
    {
        new AppSettings { Width = 1400, Height = 900, IsMaximized = true, DefaultFitMode = ZoomMode.FitWidth }.Save(StorePath);

        AppSettings loaded = AppSettings.Load(StorePath);

        Assert.Equal(1400, loaded.Width);
        Assert.Equal(900, loaded.Height);
        Assert.True(loaded.IsMaximized);
        Assert.True(loaded.HasSize);
        Assert.Equal(ZoomMode.FitWidth, loaded.DefaultFitMode);
        Assert.Contains("\"FitWidth\"", File.ReadAllText(StorePath));
    }

    [Fact]
    public void HasSize_RejectsSizesBelowTheMinimum()
    {
        Assert.False(new AppSettings { Width = 100, Height = 100 }.HasSize);
        Assert.False(new AppSettings { Width = 1280, Height = AppSettings.MinimumHeight - 1 }.HasSize);
        Assert.True(new AppSettings { Width = AppSettings.MinimumWidth, Height = AppSettings.MinimumHeight }.HasSize);
    }

    [Fact]
    public void Load_CorruptFile_GivesDefaults()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(StorePath, "{ nope");

        Assert.False(AppSettings.Load(StorePath).HasSize);
    }
}
