using System.Text.Json.Serialization;

namespace AikoPdf.Services;

/// <summary>A PDF the user opened before.</summary>
/// <param name="Path">Full path of the file.</param>
/// <param name="LastOpened">When it was last opened.</param>
public sealed record RecentFile(string Path, DateTimeOffset LastOpened)
{
    /// <summary>The file name without its folder, for display.</summary>
    [JsonIgnore]
    public string Name => System.IO.Path.GetFileName(Path);

    /// <summary>The folder the file lives in, for display under the name.</summary>
    [JsonIgnore]
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;
}
