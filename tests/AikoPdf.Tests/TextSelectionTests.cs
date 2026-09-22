using AikoPdf.Pdf;
using Windows.Foundation;
using Xunit;

namespace AikoPdf.Tests;

public class TextSelectionTests
{
    // Two lines: "Hi yo" on line 0 (words 0 and 1), "ok" on line 1 (word 2). Each glyph is 10 x 10.
    private static readonly TextGlyph[] Glyphs =
    [
        new("H", new Rect(0, 0, 10, 10), 0, 0),
        new("i", new Rect(10, 0, 10, 10), 0, 0),
        new("y", new Rect(30, 0, 10, 10), 1, 0),
        new("o", new Rect(40, 0, 10, 10), 1, 0),
        new("o", new Rect(0, 20, 10, 10), 2, 1),
        new("k", new Rect(10, 20, 10, 10), 2, 1),
    ];

    private static TextSelection Drag(Point from, Point to)
    {
        var selection = new TextSelection(Glyphs);
        selection.Begin(from);
        selection.Extend(to);
        return selection;
    }

    [Fact]
    public void Begin_AloneSelectsNothing()
    {
        var selection = new TextSelection(Glyphs);
        selection.Begin(new Point(5, 5));

        Assert.True(selection.IsEmpty);
        Assert.Equal(string.Empty, selection.GetText());
        Assert.Empty(selection.GetRects());
    }

    [Fact]
    public void Extend_WithoutBegin_IsIgnored()
    {
        var selection = new TextSelection(Glyphs);
        selection.Extend(new Point(45, 5));

        Assert.True(selection.IsEmpty);
    }

    [Fact]
    public void DragAcrossOneLine_JoinsWordsWithSpaces()
    {
        TextSelection selection = Drag(new Point(5, 5), new Point(45, 5));

        Assert.Equal("Hi yo", selection.GetText());
        Assert.Equal([new Rect(0, 0, 50, 10)], selection.GetRects());
    }

    [Fact]
    public void DragAcrossLines_BreaksLinesAndGivesOneRectPerLine()
    {
        TextSelection selection = Drag(new Point(15, 5), new Point(15, 25));

        Assert.Equal($"i yo{Environment.NewLine}ok", selection.GetText());
        Assert.Equal([new Rect(10, 0, 40, 10), new Rect(0, 20, 20, 10)], selection.GetRects());
    }

    [Fact]
    public void DraggingBackwards_SelectsTheSameRange()
    {
        Assert.Equal(Drag(new Point(15, 5), new Point(15, 25)).GetText(), Drag(new Point(15, 25), new Point(15, 5)).GetText());
    }

    [Fact]
    public void DragFromMargins_SnapsToLineEnds()
    {
        TextSelection selection = Drag(new Point(-50, 5), new Point(500, 5));

        Assert.Equal("Hi yo", selection.GetText());
    }

    [Fact]
    public void DragInGapBetweenWords_PicksNearestGlyph()
    {
        // x = 22 is closer to "i" (center 15) than to "y" (center 35).
        TextSelection selection = Drag(new Point(5, 5), new Point(22, 5));

        Assert.Equal("Hi", selection.GetText());
    }

    [Fact]
    public void PointBelowAllText_SnapsToLastLine()
    {
        TextSelection selection = Drag(new Point(5, 5), new Point(5, 500));

        Assert.Equal($"Hi yo{Environment.NewLine}o", selection.GetText());
    }

    [Fact]
    public void SelectAll_CoversEverything()
    {
        var selection = new TextSelection(Glyphs);
        selection.SelectAll();

        Assert.Equal($"Hi yo{Environment.NewLine}ok", selection.GetText());
        Assert.Equal(0, selection.Start);
        Assert.Equal(5, selection.End);
    }

    [Fact]
    public void Clear_EmptiesTheSelection()
    {
        TextSelection selection = Drag(new Point(5, 5), new Point(45, 5));
        selection.Clear();

        Assert.True(selection.IsEmpty);
        Assert.Equal(-1, selection.Start);
    }

    // Two side-by-side boxes sharing rows, listed box by box as a PDF draws them: "A1 A2" on the left, "B1 B2" on
    // the right. Each glyph is 10 x 10; rows at y 0 and 20; the left box spans x 0..20, the right box x 100..120.
    private static readonly TextGlyph[] TwoBoxes =
    [
        new("A", new Rect(0, 0, 10, 10), 0, 0),
        new("1", new Rect(10, 0, 10, 10), 0, 0),
        new("A", new Rect(0, 20, 10, 10), 1, 1),
        new("2", new Rect(10, 20, 10, 10), 1, 1),
        new("B", new Rect(100, 0, 10, 10), 2, 2),
        new("1", new Rect(110, 0, 10, 10), 2, 2),
        new("B", new Rect(100, 20, 10, 10), 3, 3),
        new("2", new Rect(110, 20, 10, 10), 3, 3),
    ];

    [Fact]
    public void DragInsideTheRightBox_StaysInTheRightBox()
    {
        var selection = new TextSelection(TwoBoxes);
        selection.Begin(new Point(105, 5));

        // Through the gap between the right box's rows (nearer the second), then past the end of its second row.
        selection.Extend(new Point(112, 16));
        Assert.Equal($"B1{Environment.NewLine}B2", selection.GetText());

        selection.Extend(new Point(200, 25));
        Assert.Equal($"B1{Environment.NewLine}B2", selection.GetText());
    }

    [Fact]
    public void DragInsideTheLeftBox_StaysInTheLeftBox()
    {
        var selection = new TextSelection(TwoBoxes);
        selection.Begin(new Point(5, 5));
        selection.Extend(new Point(15, 25));

        Assert.Equal($"A1{Environment.NewLine}A2", selection.GetText());
    }

    [Fact]
    public void PointInTheGapBetweenBoxes_SnapsToTheNearerBox()
    {
        var selection = new TextSelection(TwoBoxes);

        Assert.Equal(1, selection.HitTest(new Point(30, 5)));
        Assert.Equal(4, selection.HitTest(new Point(90, 5)));
    }

    // Two cards like a design document: a short label, a title row, and body rows of uneven length, listed card
    // by card. Card A's text ends at x 60 while its box runs to x 90; card B starts at x 100. B has one more row.
    private static readonly TextGlyph[] TwoCards =
    [
        new("a", new Rect(4, 0, 6, 6), 0, 0),                                        // label A
        new("T", new Rect(0, 10, 10, 10), 1, 1), new("A", new Rect(10, 10, 10, 10), 1, 1),
        new("r", new Rect(0, 24, 10, 10), 2, 2), new("1", new Rect(10, 24, 10, 10), 2, 2), new("x", new Rect(20, 24, 40, 10), 2, 2),
        new("r", new Rect(0, 38, 10, 10), 3, 3), new("2", new Rect(10, 38, 10, 10), 3, 3),
        new("b", new Rect(104, 0, 6, 6), 4, 4),                                      // label B
        new("T", new Rect(100, 10, 10, 10), 5, 5), new("B", new Rect(110, 10, 10, 10), 5, 5),
        new("s", new Rect(100, 24, 10, 10), 6, 6), new("1", new Rect(110, 24, 10, 10), 6, 6),
        new("s", new Rect(100, 38, 10, 10), 7, 7), new("2", new Rect(110, 38, 10, 10), 7, 7),
        new("s", new Rect(100, 52, 10, 10), 8, 8), new("3", new Rect(110, 52, 10, 10), 8, 8),
    ];

    // The drawn card backgrounds behind TwoCards: A spans x 0..90, B spans x 100..190, both y -4..66.
    private static readonly Rect[] TwoCardPanels = [new Rect(0, -4, 90, 70), new Rect(100, -4, 90, 70)];

    [Fact]
    public void EmptySpaceInsideACard_BelongsToThatCard()
    {
        var selection = new TextSelection(TwoCards, TwoCardPanels);

        // Right of card A's short title row, level with its label, and below its last row: all card A.
        Assert.Equal(2, selection.HitTest(new Point(80, 15)));
        Assert.Equal(0, selection.HitTest(new Point(80, 3)));
        Assert.Equal(7, selection.HitTest(new Point(50, 60)));

        // The natural "select this card" gesture: title top-left to the card's bottom-right corner, which is
        // nearer card B's text than card A's but still inside card A's drawn panel.
        selection.Begin(new Point(2, 12));
        selection.Extend(new Point(50, 30));
        selection.Extend(new Point(85, 58));
        Assert.Equal($"TA{Environment.NewLine}r1x{Environment.NewLine}r2", selection.GetText());
        Assert.All(selection.GetRects(), rect => Assert.True(rect.Right <= 60));
    }

    [Fact]
    public void WithoutPanels_EmptySpaceGoesToTheNearestBlock()
    {
        var selection = new TextSelection(TwoCards);

        // Nothing is drawn behind the cards, so the nearer card's row wins on each side of the gap.
        Assert.Equal(2, selection.HitTest(new Point(55, 15)));
        Assert.Equal(9, selection.HitTest(new Point(80, 15)));
    }

    [Fact]
    public void ShorterCard_KeepsItsBottomPadding()
    {
        var selection = new TextSelection(TwoCards, TwoCardPanels);
        selection.Begin(new Point(2, 12));
        selection.Extend(new Point(88, 64));

        // Level with card B's extra row, still inside card A: card A's last row, not B's.
        Assert.Equal($"TA{Environment.NewLine}r1x{Environment.NewLine}r2", selection.GetText());
    }

    [Fact]
    public void HeadingOverTwoColumns_StaysWithTheHeading()
    {
        TextGlyph[] glyphs =
        [
            new("H", new Rect(0, 0, 200, 10), 0, 0),                                  // heading across both columns
            new("L", new Rect(0, 14, 10, 10), 1, 1), new("1", new Rect(10, 14, 10, 10), 1, 1),
            new("L", new Rect(0, 28, 10, 10), 2, 2), new("2", new Rect(10, 28, 10, 10), 2, 2),
            new("R", new Rect(120, 14, 10, 10), 3, 3), new("1", new Rect(130, 14, 10, 10), 3, 3),
            new("R", new Rect(120, 28, 10, 10), 4, 4), new("2", new Rect(130, 28, 10, 10), 4, 4),
        ];
        var selection = new TextSelection(glyphs);

        Assert.Equal(0, selection.HitTest(new Point(150, 5)));
        Assert.Equal(6, selection.HitTest(new Point(150, 20)));
        Assert.Equal(2, selection.HitTest(new Point(60, 20)));
    }

    [Fact]
    public void WobbleWithinThePressedLetter_SelectsNothing()
    {
        var selection = new TextSelection(Glyphs);
        selection.Begin(new Point(4, 5));
        selection.Extend(new Point(6, 5));

        Assert.True(selection.IsEmpty);

        selection.Extend(new Point(15, 5));
        Assert.Equal("Hi", selection.GetText());
    }

    [Fact]
    public void SelectWord_TakesTheWholeWordUnderThePoint()
    {
        var selection = new TextSelection(Glyphs);

        selection.SelectWord(new Point(45, 5));
        Assert.Equal("yo", selection.GetText());

        selection.SelectWord(new Point(2, 25));
        Assert.Equal("ok", selection.GetText());
    }

    [Fact]
    public void SelectWord_InTheGapTakesTheNearerWord()
    {
        var selection = new TextSelection(Glyphs);
        selection.SelectWord(new Point(22, 5));

        Assert.Equal("Hi", selection.GetText());
    }

    [Fact]
    public void SelectParagraph_TakesACardsWholeBody_WhenItsLinesAreSetLoosely()
    {
        // Card text measured on a design document: 7.8 high, 6.5 apart, ragged right by up to 22 points. The
        // title above stops short, so it isn't part of the body.
        TextGlyph[] glyphs =
        [
            new("T", new Rect(0, 0, 100, 7.8), 0, 0),
            new("a", new Rect(0, 16.4, 190, 7.8), 1, 1),
            new("b", new Rect(0, 30.7, 212, 7.8), 2, 2),
            new("c", new Rect(0, 45.0, 171, 7.8), 3, 3),
        ];
        var selection = new TextSelection(glyphs);

        selection.SelectParagraph(new Point(50, 20));
        Assert.Equal($"a{Environment.NewLine}b{Environment.NewLine}c", selection.GetText());

        selection.SelectParagraph(new Point(50, 4));
        Assert.Equal("T", selection.GetText());
    }

    [Fact]
    public void SelectParagraph_StaysInsideTheCardUnderThePoint()
    {
        var selection = new TextSelection(TwoCards, TwoCardPanels);

        // Card A's rows, and nothing from card B or card A's much smaller label.
        selection.SelectParagraph(new Point(12, 26));
        Assert.Contains($"r1x{Environment.NewLine}r2", selection.GetText(), StringComparison.Ordinal);
        Assert.DoesNotContain("s", selection.GetText(), StringComparison.Ordinal);
        Assert.False(selection.GetText().StartsWith('a'));

        selection.SelectParagraph(new Point(112, 40));
        Assert.DoesNotContain("r", selection.GetText(), StringComparison.Ordinal);
        Assert.Contains("s", selection.GetText(), StringComparison.Ordinal);
    }

    // A page of plain text shaped like the school syllabus this was measured on: one line on its own, a two-line
    // paragraph, then a short heading with a line of body text under it. Lines are 10 high, 2.5 apart inside a
    // paragraph and 15 apart between paragraphs, all left-aligned in a column 200 wide.
    private static TextGlyph[] Syllabus()
    {
        (string Text, double Top, double Right)[] rows =
        [
            ("fourth", 0, 200),
            ("absent", 25, 200),
            ("campbell", 37.5, 150),
            ("food", 62.5, 40),
            ("water", 75, 200),
        ];

        var glyphs = new List<TextGlyph>();
        for (int line = 0; line < rows.Length; line++)
        {
            (string text, double top, double right) = rows[line];
            double width = right / text.Length;
            for (int i = 0; i < text.Length; i++)
            {
                glyphs.Add(new TextGlyph(text[i].ToString(), new Rect(i * width, top, width, 10), line, line));
            }
        }

        return [.. glyphs];
    }

    [Theory]
    [InlineData(5, "fourth")]            // a line with a blank line on either side is a paragraph of its own
    [InlineData(30, "absent|campbell")]  // a wrapped paragraph, clicked on its first line
    [InlineData(42, "absent|campbell")]  // the same paragraph, clicked on its last line
    [InlineData(67, "food")]             // a heading stops short of the margin, so it stands alone
    [InlineData(80, "water")]            // and the body under it does not reach back up into it
    public void SelectParagraph_TakesOneParagraphOfPlainText(double y, string expected)
    {
        var selection = new TextSelection(Syllabus());
        selection.SelectParagraph(new Point(20, y));

        Assert.Equal(expected.Replace("|", Environment.NewLine, StringComparison.Ordinal), selection.GetText());
    }

    [Fact]
    public void EmptyPage_IsSafeForEveryOperation()
    {
        var selection = new TextSelection([]);
        selection.Begin(new Point(5, 5));
        selection.Extend(new Point(50, 50));
        selection.SelectAll();
        selection.SelectWord(new Point(5, 5));
        selection.SelectParagraph(new Point(5, 5));

        Assert.True(selection.IsEmpty);
        Assert.Equal(-1, selection.HitTest(new Point(5, 5)));
        Assert.Empty(selection.GetRects());
        Assert.Equal(string.Empty, selection.GetText());
    }
}
