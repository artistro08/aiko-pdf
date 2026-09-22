using AikoPdf.Pdf;
using Windows.Foundation;
using Xunit;
using Xunit.Abstractions;

namespace AikoPdf.Tests;

/// <summary>
/// Hit testing over a real laid-out document.
///
/// Cards sitting side by side share rows, so a point in one card's padding is usually nearer the neighbor's
/// letters than its own. This sweep walks a grid of points inside every card drawn on every page and requires
/// each one to resolve to that card's text, which is what keeps a drag inside one card out of the card beside it.
/// </summary>
/// <remarks>
/// @author Devin Green (Artistro08)
/// </remarks>
public class RealDocumentSelectionTests(ITestOutputHelper output)
{
    // Grid steps across and down each card, as fractions of its size.
    private const int Steps = 6;

    // A card counts as holding a block of text at this many glyphs over this many lines; below that it is a chip,
    // a badge or a drop shadow, and the nearest text outside it is the right answer for a point inside it.
    private const int MinimumGlyphs = 8;
    private const int MinimumLines  = 2;

    /// <summary>
    /// The document to sweep: <c>AIKO_TEST_PDF</c> if it is set, otherwise the multi-column design document this
    /// was written against. The test passes when neither is present, so a fresh clone still runs green.
    /// </summary>
    /// <returns>A path to an existing PDF, or null.</returns>
    private static string? DocumentPath()
    {
        string fromEnvironment = Environment.GetEnvironmentVariable("AIKO_TEST_PDF") ?? string.Empty;
        if (File.Exists(fromEnvironment))
        {
            return fromEnvironment;
        }

        string client = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            @"My Drive\Projects\Clients\One Community Global\Design Language\One Community Global - Design Language (Dark).pdf");

        return File.Exists(client) ? client : null;
    }

    [Fact]
    public void EveryPointInsideACardResolvesToThatCard()
    {
        if (DocumentPath() is not { } path)
        {
            output.WriteLine("No document to sweep; set AIKO_TEST_PDF to run this.");
            return;
        }

        using PdfTextLayer layer = PdfTextLayer.Open(path);
        int checks   = 0;
        int failures = 0;

        for (int page = 1; page <= layer.PageCount; page++)
        {
            IReadOnlyList<TextGlyph> glyphs = layer.GetGlyphs(page);
            IReadOnlyList<Rect>      panels = layer.GetContainers(page);
            if ((glyphs.Count == 0) || (panels.Count == 0))
            {
                continue;
            }

            List<Rect> cards = TextHoldingPanels(glyphs, panels);
            foreach (Rect card in cards)
            {
                for (int sx = 1; sx <= Steps; sx++)
                {
                    for (int sy = 1; sy <= Steps; sy++)
                    {
                        var point = new Point(
                            card.Left + (card.Width * sx / (Steps + 1.0)),
                            card.Top + (card.Height * sy / (Steps + 1.0)));

                        var selection = new TextSelection(glyphs, panels);
                        int hit       = selection.HitTest(point);
                        checks++;

                        if ((hit < 0) || Inside(card, glyphs[hit]) || InsideAnOverlappingCard(cards, card, glyphs[hit]))
                        {
                            continue;
                        }

                        failures++;
                        output.WriteLine($"page {page} card {card.Left:F0},{card.Top:F0} {card.Width:F0}x{card.Height:F0}: "
                            + $"point ({point.X:F0},{point.Y:F0}) resolved to '{glyphs[hit].Text}' at {glyphs[hit].Bounds.Left:F0},{glyphs[hit].Bounds.Top:F0}");
                    }
                }
            }
        }

        output.WriteLine($"{checks} points checked, {failures} resolved outside their card");
        Assert.Equal(0, failures);
        Assert.True(checks > 0, "the document has no cards to sweep");
    }

    /// <summary>Picks out the panels on a page that hold a block of text.</summary>
    /// <param name="glyphs">The page's glyphs.</param>
    /// <param name="panels">The page's drawn rectangles.</param>
    /// <returns>The rectangles that behave like cards or columns.</returns>
    private static List<Rect> TextHoldingPanels(IReadOnlyList<TextGlyph> glyphs, IReadOnlyList<Rect> panels)
    {
        var cards = new List<Rect>();
        foreach (Rect panel in panels)
        {
            var linesInside = new HashSet<int>();
            int count       = 0;
            foreach (TextGlyph glyph in glyphs)
            {
                if (Inside(panel, glyph))
                {
                    count++;
                    linesInside.Add(glyph.LineIndex);
                }
            }

            if ((count >= MinimumGlyphs) && (linesInside.Count >= MinimumLines))
            {
                cards.Add(panel);
            }
        }

        return cards;
    }

    /// <summary>
    /// True when a glyph belongs to a card that touches this one. A badge or chip drawn inside a card is part of
    /// that card, and a card's background is often stacked from rectangles a point or two apart, so those are not
    /// a different card and picking their text is correct.
    /// </summary>
    /// <param name="cards">Every card on the page.</param>
    /// <param name="card">The card the point was inside.</param>
    /// <param name="glyph">The glyph the point resolved to.</param>
    /// <returns>True when the glyph is in a card overlapping <paramref name="card"/>.</returns>
    private static bool InsideAnOverlappingCard(List<Rect> cards, Rect card, TextGlyph glyph)
    {
        foreach (Rect other in cards)
        {
            if (Overlaps(other, card) && Inside(other, glyph))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Inside(Rect panel, TextGlyph glyph)
    {
        double x = glyph.Bounds.Left + (glyph.Bounds.Width / 2);
        double y = glyph.Bounds.Top + (glyph.Bounds.Height / 2);
        return (x >= panel.Left) && (x <= panel.Right) && (y >= panel.Top) && (y <= panel.Bottom);
    }

    private static bool Overlaps(Rect a, Rect b) =>
        (a.Left < b.Right) && (b.Left < a.Right) && (a.Top < b.Bottom) && (b.Top < a.Bottom);
}
