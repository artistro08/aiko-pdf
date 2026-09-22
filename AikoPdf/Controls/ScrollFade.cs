using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI;

namespace AikoPdf.Controls;

/// <summary>
/// Fades out the top and bottom edges of a scrolling area, each only while there is more to see past it: the top
/// once the list has scrolled down, the bottom while anything is still below.
///
/// The fade is a gradient mask over a live copy of the scrolling element, drawn by the compositor. The element
/// itself goes invisible but stays where it is, so it still scrolls, hovers and takes clicks; what the reader sees
/// is the masked copy on top. The mask covers the element's whole area, so content scrolls all the way to the
/// panel's edge and dissolves there instead of stopping short at a hard line.
/// </summary>
/// <remarks>
/// @author Devin Green (Artistro08)
/// @link https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.composition.compositionmaskbrush
/// </remarks>
public sealed class ScrollFade
{
    // How quickly an edge's fade comes and goes as the list reaches or leaves that end.
    private static readonly TimeSpan EdgeDuration = TimeSpan.FromMilliseconds(150);

    private static readonly Color Shown  = Color.FromArgb(255, 255, 255, 255);
    private static readonly Color Hidden = Color.FromArgb(0, 255, 255, 255);

    private readonly FrameworkElement source;
    private readonly ScrollViewer     scroller;
    private readonly double           topHold;
    private readonly double           topLength;
    private readonly double           bottomLength;

    private readonly Compositor                   compositor;
    private readonly CompositionVisualSurface     surface;
    private readonly SpriteVisual                 sprite;
    private readonly CompositionColorGradientStop topEdge;
    private readonly CompositionColorGradientStop topHoldEnd;
    private readonly CompositionColorGradientStop topInner;
    private readonly CompositionColorGradientStop bottomInner;
    private readonly CompositionColorGradientStop bottomEdge;

    private bool topFaded;
    private bool bottomFaded;

    private ScrollFade(FrameworkElement source, ScrollViewer scroller, FrameworkElement host, double topHold, double topLength, double bottomLength)
    {
        this.source       = source;
        this.scroller     = scroller;
        this.topHold      = topHold;
        this.topLength    = topLength;
        this.bottomLength = bottomLength;

        Visual visual = ElementCompositionPreview.GetElementVisual(source);
        compositor = visual.Compositor;

        topEdge     = compositor.CreateColorGradientStop(0, Shown);
        topHoldEnd  = compositor.CreateColorGradientStop(0, Shown);
        topInner    = compositor.CreateColorGradientStop(0, Shown);
        bottomInner = compositor.CreateColorGradientStop(1, Shown);
        bottomEdge  = compositor.CreateColorGradientStop(1, Shown);

        CompositionLinearGradientBrush gradient = compositor.CreateLinearGradientBrush();
        gradient.MappingMode = CompositionMappingMode.Relative;
        gradient.StartPoint  = new Vector2(0, 0);
        gradient.EndPoint    = new Vector2(0, 1);
        gradient.ColorStops.Add(topEdge);
        gradient.ColorStops.Add(topHoldEnd);
        gradient.ColorStops.Add(topInner);
        gradient.ColorStops.Add(bottomInner);
        gradient.ColorStops.Add(bottomEdge);

        surface = compositor.CreateVisualSurface();
        surface.SourceVisual = visual;

        CompositionMaskBrush mask = compositor.CreateMaskBrush();
        mask.Source = compositor.CreateSurfaceBrush(surface);
        mask.Mask   = gradient;

        sprite = compositor.CreateSpriteVisual();
        sprite.Brush = mask;

        // The element stays in place for input; the masked copy is what shows.
        visual.Opacity = 0;
        ElementCompositionPreview.SetElementChildVisual(host, sprite);

        source.SizeChanged    += (_, _) => Resize();
        scroller.ViewChanged  += (_, _) => UpdateEdges(animate: true);
        scroller.SizeChanged  += (_, _) => UpdateEdges(animate: false);
        if (scroller.Content is FrameworkElement content)
        {
            content.SizeChanged += (_, _) => UpdateEdges(animate: false);
        }

        Resize();
    }

    /// <summary>Fades the edges of a scrolling element.</summary>
    /// <param name="source">The element to fade: the scroller itself, or a control that scrolls (a ListView).</param>
    /// <param name="scroller">The scroll viewer that moves its content.</param>
    /// <param name="host">An element laid over <paramref name="source"/>, the same size, that shows the faded copy.</param>
    /// <param name="topLength">How far the top fade reaches, in device-independent pixels.</param>
    /// <param name="bottomLength">How far the bottom fade reaches, in device-independent pixels.</param>
    /// <param name="topHold">
    /// A band at the very top kept fully clear before the fade begins, for a heading drawn over the list: content
    /// scrolling up is gone by the time it reaches the heading instead of showing through behind it.
    /// </param>
    /// <returns>The fade, which lives as long as the elements do.</returns>
    public static ScrollFade Attach(FrameworkElement source, ScrollViewer scroller, FrameworkElement host, double topLength, double bottomLength, double topHold = 0)
        => new(source, scroller, host, topHold, topLength, bottomLength);

    private void Resize()
    {
        var size = new Vector2((float)source.ActualWidth, (float)source.ActualHeight);
        surface.SourceSize = size;
        sprite.Size        = size;

        if (size.Y > 0)
        {
            topHoldEnd.Offset  = (float)Math.Clamp(topHold / size.Y, 0, 0.5);
            topInner.Offset    = (float)Math.Clamp((topHold + topLength) / size.Y, 0, 0.5);
            bottomInner.Offset = (float)Math.Clamp(1 - (bottomLength / size.Y), 0.5, 1);
        }

        UpdateEdges(animate: false);
    }

    /// <summary>Shows each edge's fade only while there is content past that edge.</summary>
    private void UpdateEdges(bool animate)
    {
        bool top    = scroller.VerticalOffset > 0.5;
        bool bottom = scroller.VerticalOffset < scroller.ScrollableHeight - 0.5;

        if (top != topFaded)
        {
            topFaded = top;
            SetEdge(topEdge, top, animate);
            SetEdge(topHoldEnd, top, animate);
        }

        if (bottom != bottomFaded)
        {
            bottomFaded = bottom;
            SetEdge(bottomEdge, bottom, animate);
        }
    }

    private void SetEdge(CompositionColorGradientStop edge, bool faded, bool animate)
    {
        Color target = faded ? Hidden : Shown;
        if (!animate)
        {
            edge.StopAnimation("Color");
            edge.Color = target;
            return;
        }

        ColorKeyFrameAnimation animation = compositor.CreateColorKeyFrameAnimation();
        animation.InsertKeyFrame(1, target);
        animation.Duration = EdgeDuration;
        edge.StartAnimation("Color", animation);
    }
}
