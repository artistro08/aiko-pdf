using System.Text.Json;
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

/// <summary>
/// The list of recently opened PDFs shown on the home page, most recent first, persisted as JSON under
/// %LOCALAPPDATA%\AikoPdf. The store path is injectable so tests never touch the real list.
/// </summary>
/// <remarks>
/// @author Devin Green (Artistro08)
/// </remarks>
public sealed class RecentFiles
{
    /// <summary>How many files the list keeps.</summary>
    public const int Capacity = 20;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string           storePath;
    private readonly List<RecentFile> items = [];

    /// <summary>Creates an empty list backed by the given file. Use <see cref="Load"/> to read an existing one.</summary>
    /// <param name="storePath">Where the JSON is saved.</param>
    public RecentFiles(string storePath)
    {
        this.storePath = storePath;
    }

    /// <summary>The files, most recent first.</summary>
    public IReadOnlyList<RecentFile> Items => items;

    /// <summary>Raised after the list changes, so the home page can refresh.</summary>
    public event Action? Changed;

    /// <summary>The default store location for this Windows user.</summary>
    public static string DefaultStorePath
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AikoPdf", "recent.json");

    /// <summary>Reads the list from disk. A missing or unreadable file gives an empty list rather than a failed start.</summary>
    /// <param name="storePath">Where the JSON lives.</param>
    /// <returns>The loaded list.</returns>
    public static RecentFiles Load(string storePath)
    {
        var recent = new RecentFiles(storePath);
        if (!File.Exists(storePath))
        {
            return recent;
        }

        try
        {
            List<RecentFile?>? saved = JsonSerializer.Deserialize<List<RecentFile?>>(File.ReadAllText(storePath), JsonOptions);
            if (saved is not null)
            {
                // A hand-edited or half-written file can hold nulls and blank paths; both are dropped here rather
                // than thrown at the home page, which reads this list while the window is coming up.
                recent.items.AddRange(saved
                    .Where(f => !string.IsNullOrWhiteSpace(f?.Path))
                    .Select(f => f!)
                    .Take(Capacity));
            }
        }
        catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
        {
            // This runs from a static initializer, so anything thrown here would stop the app from starting at
            // all. A corrupt list is not worth that; it rebuilds as files are opened.
        }

        return recent;
    }

    /// <summary>Puts a file at the top of the list, stamped with the current time.</summary>
    /// <param name="path">Full path of the file just opened.</param>
    public void Add(string path)
    {
        Reload();
        string full = Full(path);
        items.RemoveAll(f => string.Equals(f.Path, full, StringComparison.OrdinalIgnoreCase));
        items.Insert(0, new RecentFile(full, DateTimeOffset.Now));
        if (items.Count > Capacity)
        {
            items.RemoveRange(Capacity, items.Count - Capacity);
        }

        Save();
    }

    /// <summary>Removes a file from the list, for example because it no longer exists.</summary>
    /// <param name="path">Path of the file, in any form <see cref="Path.GetFullPath(string)"/> accepts.</param>
    public void Remove(string path)
    {
        Reload();
        string full = Full(path);
        if (items.RemoveAll(f => string.Equals(f.Path, full, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            Save();
        }
    }

    /// <summary>
    /// Drops entries whose files were deleted or moved since they were opened. An entry on a drive that is simply
    /// not connected right now stays: a network share or a memory stick comes back, and forgetting the file the
    /// moment it is unplugged would quietly empty the list.
    /// </summary>
    public void Prune()
    {
        Reload();
        if (items.RemoveAll(f => VolumeIsPresent(f.Path) && !File.Exists(f.Path)) > 0)
        {
            Save();
        }
    }

    /// <summary>
    /// Picks up what other windows have written before changing the list. Each open document is its own process
    /// and they all share one file, so without this the last one to save would drop everything the others added.
    /// </summary>
    private void Reload()
    {
        RecentFiles saved = Load(storePath);
        items.Clear();
        items.AddRange(saved.items);
    }

    /// <summary>True when the drive or share an entry lives on is reachable at the moment.</summary>
    /// <param name="path">Full path of the file.</param>
    /// <returns>True when its folder can be seen, or when the answer cannot be worked out.</returns>
    private static bool VolumeIsPresent(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(path);
            return string.IsNullOrEmpty(root) || Directory.Exists(root);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Normalizes a path, leaving it alone when Windows rejects it.</summary>
    /// <param name="path">The path to normalize.</param>
    /// <returns>The full path, or the original when it cannot be expanded.</returns>
    private static string Full(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);

            // Write beside, then swap, so a crash mid-write can't leave a half-written list.
            string temp = storePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(items, JsonOptions));
            File.Move(temp, storePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the recent list is a nuisance, not a reason to fail the open.
        }

        Changed?.Invoke();
    }
}
