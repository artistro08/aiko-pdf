using AikoPdf.Pdf;
using Xunit;

namespace AikoPdf.Tests;

public class ZoomTests
{
    [Fact]
    public void FitWidth_UsesViewportWidthMinusPadding()
    {
        double scale = Zoom.Fit(ZoomMode.FitWidth, 612, 792, 1000, 400, 20, 1.0);

        Assert.Equal(960.0 / 612.0, scale, 6);
    }

    [Fact]
    public void FitPage_UsesTheSmallerOfWidthAndHeightFit()
    {
        double scale = Zoom.Fit(ZoomMode.FitPage, 612, 792, 1000, 400, 20, 1.0);

        Assert.Equal(360.0 / 792.0, scale, 6);
    }

    [Fact]
    public void Custom_ReturnsCurrentUnchanged()
    {
        Assert.Equal(1.75, Zoom.Fit(ZoomMode.Custom, 612, 792, 1000, 400, 20, 1.75));
    }

    [Fact]
    public void Fit_WithUnusableViewport_KeepsCurrent()
    {
        Assert.Equal(1.5, Zoom.Fit(ZoomMode.FitWidth, 612, 792, 10, 10, 20, 1.5));
        Assert.Equal(1.5, Zoom.Fit(ZoomMode.FitWidth, 0, 792, 1000, 400, 20, 1.5));
    }

    [Fact]
    public void Fit_IsClamped()
    {
        Assert.Equal(Zoom.Max, Zoom.Fit(ZoomMode.FitWidth, 10, 10, 10000, 10000, 0, 1.0));
        Assert.Equal(Zoom.Min, Zoom.Fit(ZoomMode.FitWidth, 10000, 10000, 100, 100, 0, 1.0));
    }

    [Theory]
    [InlineData(1.0, 1.25)]
    [InlineData(1.1, 1.25)]
    [InlineData(8.0, 8.0)]
    [InlineData(0.1, 0.25)]
    public void StepIn_MovesToNextPreset(double current, double expected)
    {
        Assert.Equal(expected, Zoom.StepIn(current));
    }

    [Theory]
    [InlineData(1.0, 0.75)]
    [InlineData(1.1, 1.0)]
    [InlineData(0.25, 0.25)]
    [InlineData(10.0, 8.0)]
    public void StepOut_MovesToPreviousPreset(double current, double expected)
    {
        Assert.Equal(expected, Zoom.StepOut(current));
    }

    [Theory]
    [InlineData(0.25, 0)]
    [InlineData(8.0, 100)]
    [InlineData(1.0, 40)]
    public void Slider_MapsEndsAndUnity(double scale, double expected)
    {
        Assert.Equal(expected, Zoom.SliderFromScale(scale), 6);
    }

    [Theory]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(2.0)]
    [InlineData(8.0)]
    public void Slider_RoundTrips(double scale)
    {
        Assert.Equal(scale, Zoom.ScaleFromSlider(Zoom.SliderFromScale(scale)), 6);
    }

    [Fact]
    public void Slider_ClampsOutOfRange()
    {
        Assert.Equal(Zoom.Min, Zoom.ScaleFromSlider(-10));
        Assert.Equal(Zoom.Max, Zoom.ScaleFromSlider(500));
        Assert.Equal(0, Zoom.SliderFromScale(0.01));
    }

    [Fact]
    public void FormatPercent_RoundsToWholePercent()
    {
        Assert.Equal("150%", Zoom.FormatPercent(1.5));
        Assert.Equal("67%", Zoom.FormatPercent(0.666));
    }
}
