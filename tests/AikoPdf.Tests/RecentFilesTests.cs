using AikoPdf.Services;
using Xunit;

namespace AikoPdf.Tests;

public sealed class RecentFilesTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), $"aikopdf-recent-{Guid.NewGuid():N}");

    private string StorePath => Path.Combine(folder, "recent.json");

    public void Dispose()
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Add_PutsNewestFirstAndDeduplicatesIgnoringCase()
    {
        var recent = new RecentFiles(StorePath);
        recent.Add(@"C:\docs\a.pdf");
        recent.Add(@"C:\docs\b.pdf");
        recent.Add(@"C:\DOCS\A.PDF");

        Assert.Equal([@"C:\DOCS\A.PDF", @"C:\docs\b.pdf"], recent.Items.Select(f => f.Path));
        Assert.Equal("A.PDF", recent.Items[0].Name);
        Assert.Equal(@"C:\DOCS", recent.Items[0].Folder);
    }

    [Fact]
    public void Add_TrimsToCapacity()
    {
        var recent = new RecentFiles(StorePath);
        for (var i = 0; i < RecentFiles.Capacity + 5; i++)
        {
            recent.Add($@"C:\docs\{i}.pdf");
        }

        Assert.Equal(RecentFiles.Capacity, recent.Items.Count);
        Assert.Equal($@"C:\docs\{RecentFiles.Capacity + 4}.pdf", recent.Items[0].Path);
    }

    [Fact]
    public void Load_RoundTripsWhatWasSaved()
    {
        var recent = new RecentFiles(StorePath);
        recent.Add(@"C:\docs\a.pdf");
        recent.Add(@"C:\docs\b.pdf");

        RecentFiles loaded = RecentFiles.Load(StorePath);

        Assert.Equal(recent.Items.Select(f => f.Path), loaded.Items.Select(f => f.Path));
        Assert.Equal(recent.Items[0].LastOpened, loaded.Items[0].LastOpened);
    }

    [Fact]
    public void Load_MissingOrCorruptFile_GivesEmptyList()
    {
        Assert.Empty(RecentFiles.Load(StorePath).Items);

        Directory.CreateDirectory(folder);
        File.WriteAllText(StorePath, "{ not json");

        Assert.Empty(RecentFiles.Load(StorePath).Items);
    }

    [Fact]
    public void Remove_DropsTheEntryAndSaves()
    {
        var recent = new RecentFiles(StorePath);
        recent.Add(@"C:\docs\a.pdf");
        recent.Add(@"C:\docs\b.pdf");
        recent.Remove(@"c:\docs\A.pdf");

        Assert.Equal([@"C:\docs\b.pdf"], recent.Items.Select(f => f.Path));
        Assert.Equal([@"C:\docs\b.pdf"], RecentFiles.Load(StorePath).Items.Select(f => f.Path));
    }

    [Fact]
    public void Prune_RemovesFilesThatNoLongerExist()
    {
        Directory.CreateDirectory(folder);
        string existing = Path.Combine(folder, "keep.pdf");
        File.WriteAllText(existing, "x");

        var recent = new RecentFiles(StorePath);
        recent.Add(existing);
        recent.Add(Path.Combine(folder, "gone.pdf"));
        recent.Prune();

        Assert.Equal([existing], recent.Items.Select(f => f.Path));
    }

    [Fact]
    public void Changed_FiresOnEveryWrite()
    {
        var recent = new RecentFiles(StorePath);
        var fired  = 0;
        recent.Changed += (_, _) => fired++;

        recent.Add(@"C:\docs\a.pdf");
        recent.Remove(@"C:\docs\a.pdf");
        recent.Remove(@"C:\docs\missing.pdf");

        Assert.Equal(2, fired);
    }

    [Fact]
    public void Add_KeepsWhatAnotherWindowSavedInTheMeantime()
    {
        var first  = new RecentFiles(StorePath);
        var second = new RecentFiles(StorePath);
        first.Add(@"C:\docs\one.pdf");

        // The second list was loaded before that and knows nothing about it, as a second window would not.
        second.Add(@"C:\docs\two.pdf");

        RecentFiles reloaded = RecentFiles.Load(StorePath);
        Assert.Equal(2, reloaded.Items.Count);
        Assert.Contains(reloaded.Items, f => f.Name == "one.pdf");
        Assert.Contains(reloaded.Items, f => f.Name == "two.pdf");
    }
}
