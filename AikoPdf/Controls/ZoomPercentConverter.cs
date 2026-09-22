using AikoPdf.Pdf;
using Microsoft.UI.Xaml.Data;

namespace AikoPdf.Controls;

/// <summary>
/// Turns a zoom slider position into the percentage shown in the thumb's tooltip, so dragging reads "125%"
/// instead of the slider's internal 0..100 value.
/// </summary>
/// <remarks>
/// @author Devin Green (Artistro08)
/// </remarks>
public sealed partial class ZoomPercentConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object value, Type targetType, object parameter, string language)
        => Zoom.FormatPercent(Zoom.ScaleFromSlider(value is double position ? position : 0));

    /// <inheritdoc/>
    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException("The zoom tooltip is display only.");
}
