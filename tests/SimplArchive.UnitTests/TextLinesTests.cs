using SimplArchive.Presentation;

namespace SimplArchive.UnitTests;

// Line-start arithmetic for the text preview's gutter (#1317, SimplArchive.Presentation.TextLines): both
// clients number the same text from this one answer, so what these pin is the numbering the user compares
// against their editor's — most of all the trailing-newline case, where off-by-one is the classic drift.
public class TextLinesTests
{
    [Fact]
    public void Null_has_no_lines_and_empty_has_one()
    {
        Assert.Empty(TextLines.Starts(null));
        Assert.Equal([0], TextLines.Starts(string.Empty));
    }

    [Fact]
    public void Starts_follow_every_newline()
    {
        Assert.Equal([0], TextLines.Starts("no newline"));
        Assert.Equal([0, 4, 8], TextLines.Starts("one\ntwo\nthree"));
    }

    [Fact]
    public void A_trailing_newline_yields_a_final_empty_line()
    {
        // Deliberate: that is the line the caret would sit on, and the one a log file's tail visibly has.
        Assert.Equal([0, 4], TextLines.Starts("one\n"));
    }

    [Fact]
    public void Carriage_returns_do_not_split()
    {
        // The split is '\n' only, matching how the panes and TextFind already count lines — a '\r' stays
        // inside its line rather than inventing a numbering scheme the rendered text does not have.
        Assert.Equal([0, 5], TextLines.Starts("one\r\ntwo"));
    }
}
