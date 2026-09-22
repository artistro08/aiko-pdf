using AikoPdf.Pdf;
using Windows.Foundation;
using Xunit;

namespace AikoPdf.Tests;

public class PageGeometryTests
{
    private static readonly PageGeometry Letter = new(0, 0, 612, 792);

    [Fact]
    public void FlipsYToTopLeftOrigin()
    {
        Rect rect = Letter.ToRendered(72, 700, 172, 724);

        Assert.Equal(new Rect(72, 68, 100, 24), rect);
        Assert.Equal(new Size(612, 792), Letter.RenderedSize);
    }

    [Fact]
    public void AcceptsEdgesInAnyOrder()
    {
        Assert.Equal(Letter.ToRendered(72, 700, 172, 724), Letter.ToRendered(172, 724, 72, 700));
    }

    [Fact]
    public void CropBoxOffset_IsSubtracted()
    {
        var cropped = new PageGeometry(100, 50, 712, 842);

        Assert.Equal(new Rect(0, 0, 100, 42), cropped.ToRendered(100, 800, 200, 842));
        Assert.Equal(new Size(612, 792), cropped.RenderedSize);
    }
}
