using System.Globalization;

namespace AikoPdf.Pdf;

/// <summary>
/// Zoom math for the viewer: fit-to-viewport scales, stepping through preset levels and clamping. A scale of 1.0
/// shows one PDF point as one device-independent pixel.
/// </summary>
/// <remarks>
/// @author Devin Green (Artistro08)
/// </remarks>
public static class Zoom
{
    /// <summary>Smallest allowed scale.</summary>
    public const double Min = 0.25;

    /// <summary>Largest allowed scale.</summary>
    public const double Max = 8.0;

    /// <summary>The levels the zoom in and zoom out buttons step through.</summary>
    public static IReadOnlyList<double> Presets { get; } = [0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 3.0, 4.0, 8.0];

    /// <summary>Keeps a scale inside the allowed range.</summary>
    /// <param name="scale">Any scale.</param>
    /// <returns>The scale, limited to <see cref="Min"/>..<see cref="Max"/>.</returns>
    public static double Clamp(double scale) => Math.Clamp(scale, Min, Max);

    /// <summary>The scale that makes a page of the given size fit the viewport in the given mode.</summary>
    /// <param name="mode">Fit width or fit page. <see cref="ZoomMode.Custom"/> returns <paramref name="current"/> unchanged.</param>
    /// <param name="pageWidth">Widest page width in points.</param>
    /// <param name="pageHeight">Tallest page height in points.</param>
    /// <param name="viewportWidth">Available width in device-independent pixels.</param>
    /// <param name="viewportHeight">Available height in device-independent pixels.</param>
    /// <param name="padding">
    /// Space to leave left, right and below the page, in device-independent pixels. None is left above: the
    /// page sits flush with the top of the view.
    /// </param>
    /// <param name="current">The scale in force, returned for custom mode or when the inputs are unusable.</param>
    /// <returns>The clamped fit scale.</returns>
    public static double Fit(ZoomMode mode, double pageWidth, double pageHeight, double viewportWidth, double viewportHeight, double padding, double current)
    {
        if ((mode == ZoomMode.Custom) || (pageWidth <= 0) || (pageHeight <= 0))
        {
            return Clamp(current);
        }

        double availableWidth  = viewportWidth  - (2 * padding);
        double availableHeight = viewportHeight - padding;
        if ((availableWidth <= 0) || (availableHeight <= 0))
        {
            return Clamp(current);
        }

        double byWidth  = availableWidth / pageWidth;
        double byHeight = availableHeight / pageHeight;

        return Clamp((mode == ZoomMode.FitWidth) ? byWidth : Math.Min(byWidth, byHeight));
    }

    /// <summary>The next preset above the current scale.</summary>
    /// <param name="current">The scale in force.</param>
    /// <returns>The first preset strictly greater than <paramref name="current"/>, or <see cref="Max"/>.</returns>
    public static double StepIn(double current)
    {
        foreach (double preset in Presets)
        {
            if (preset > current + 0.001)
            {
                return preset;
            }
        }

        return Max;
    }

    /// <summary>The next preset below the current scale.</summary>
    /// <param name="current">The scale in force.</param>
    /// <returns>The last preset strictly less than <paramref name="current"/>, or <see cref="Min"/>.</returns>
    public static double StepOut(double current)
    {
        for (int i = Presets.Count - 1; i >= 0; i--)
        {
            if (Presets[i] < current - 0.001)
            {
                return Presets[i];
            }
        }

        return Min;
    }

    /// <summary>
    /// Maps a scale onto the zoom slider's 0..100 range. The mapping is logarithmic so that 100% sits near the
    /// middle and each slider step feels the same whether the view is small or large.
    /// </summary>
    /// <param name="scale">A scale in <see cref="Min"/>..<see cref="Max"/>.</param>
    /// <returns>A slider value in 0..100.</returns>
    public static double SliderFromScale(double scale)
        => 100 * Math.Log(Clamp(scale) / Min) / Math.Log(Max / Min);

    /// <summary>The scale a slider position stands for; the inverse of <see cref="SliderFromScale"/>.</summary>
    /// <param name="value">A slider value in 0..100.</param>
    /// <returns>The clamped scale.</returns>
    public static double ScaleFromSlider(double value)
        => Clamp(Min * Math.Pow(Max / Min, Math.Clamp(value, 0, 100) / 100));

    /// <summary>Formats a scale for the zoom box: 1.5 becomes "150%".</summary>
    /// <param name="scale">The scale in force.</param>
    /// <returns>A whole-number percentage with a percent sign.</returns>
    public static string FormatPercent(double scale)
        => string.Create(CultureInfo.CurrentCulture, $"{Math.Round(scale * 100)}%");
}
