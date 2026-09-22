using AikoPdf.Services;
using Xunit;

namespace AikoPdf.Tests;

public sealed class LaunchArgumentsTests : IDisposable
{
    private readonly string pdf = SamplePdf.WriteTemp(SamplePdf.TwoLines());

    public void Dispose() => File.Delete(pdf);

    [Fact]
    public void FileFrom_SkipsTheExecutable()
    {
        string exe = Environment.ProcessPath!;

        Assert.Equal(pdf, LaunchArguments.FileFrom([exe, pdf]));
    }

    [Fact]
    public void FileFrom_TakesTheFileWhenTheExecutableIsLeftOff()
    {
        Assert.Equal(pdf, LaunchArguments.FileFrom([pdf]));
    }

    [Fact]
    public void FileFrom_IgnoresFlagsAndMissingFiles()
    {
        Assert.Equal(pdf, LaunchArguments.FileFrom(["--light", @"C:\nowhere\gone.pdf", pdf]));
    }

    [Fact]
    public void FileFrom_IsNullWithoutAFile()
    {
        Assert.Null(LaunchArguments.FileFrom([Environment.ProcessPath!, "--dark"]));
        Assert.Null(LaunchArguments.FileFrom([]));
    }
}
