using System.Security.Cryptography;
using System.Text;
using AikoPdf.Pdf;
using Windows.Storage.Streams;

namespace AikoPdf.Services;

/// <summary>
/// First-page previews for the home page, saved as PNG files under %LOCALAPPDATA%\AikoPdf\thumbs the moment a
/// document is opened. The home page then shows them without touching the PDFs again, so it stays instant even
/// when a recent file sits on a slow drive or has gone missing.
/// </summary>
/// <remarks>
/// @author Devin Green (Artistro08)
/// </remarks>
public static class ThumbnailCache
{
    /// <summary>Preview width in device-independent pixels, matching the card on the home page.</summary>
    public const double DisplayWidth = 178;

    /// <summary>The default folder for this Windows user.</summary>
    public static string DefaultFolder
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AikoPdf", "thumbs");

    /// <summary>Where a PDF's preview lives: one file per path, case-insensitive, so a re-open overwrites in place.</summary>
    /// <param name="pdfPath">Full path of the PDF.</param>
    /// <param name="folder">The cache folder; <see cref="DefaultFolder"/> when omitted.</param>
    /// <returns>The PNG path, whether or not it exists yet.</returns>
    public static string PathFor(string pdfPath, string? folder = null)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(pdfPath).ToUpperInvariant()));
        return Path.Combine(folder ?? DefaultFolder, $"{Convert.ToHexString(hash)[..32]}.png");
    }

    /// <summary>Renders the first page and writes it to the cache.</summary>
    /// <param name="renderer">The open document.</param>
    /// <param name="pdfPath">Full path of the PDF, used to name the file.</param>
    /// <param name="rasterizationScale">The display's scale, so the preview is crisp at high DPI.</param>
    /// <param name="folder">The cache folder; <see cref="DefaultFolder"/> when omitted.</param>
    /// <returns>The written PNG path.</returns>
    /// <exception cref="IOException">The cache folder can't be written.</exception>
    public static async Task<string> SaveAsync(PdfRenderer renderer, string pdfPath, double rasterizationScale, string? folder = null)
    {
        string target = PathFor(pdfPath, folder);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        using IRandomAccessStream png = await renderer.RenderAsync(1, DisplayWidth * rasterizationScale);

        // Write beside, then swap, so the home page never reads a half-written image.
        string temp = target + ".tmp";
        await using (FileStream file = File.Create(temp))
        {
            await png.AsStreamForRead().CopyToAsync(file);
        }

        File.Move(temp, target, overwrite: true);
        return target;
    }

    /// <summary>
    /// Deletes previews for files no longer in the recent list, and any half-written ones a crash left behind.
    /// Called after the list changes, so the folder tracks the twenty files the home page can actually show.
    /// </summary>
    /// <param name="keep">Full paths of the PDFs whose previews should stay.</param>
    /// <param name="folder">The cache folder; <see cref="DefaultFolder"/> when omitted.</param>
    public static void Prune(IEnumerable<string> keep, string? folder = null)
    {
        string cache = folder ?? DefaultFolder;
        if (!Directory.Exists(cache))
        {
            return;
        }

        var wanted = new HashSet<string>(keep.Select(path => PathFor(path, cache)), StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(cache))
        {
            if (wanted.Contains(file))
            {
                continue;
            }

            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Someone is reading it right now; it will go on the next open.
            }
        }
    }
}
