using SimplArchive.Presentation;

namespace SimplArchive.UnitTests;

// The shared answer for showing and typing a document date's optional UTC time (ADR "Optional time on the
// document date"). Both clients render and parse through this, so the keyboard-first spellings and the
// display shape are pinned here once.
public class DocumentDateFormatTests
{
    [Fact]
    public void Display_shows_the_date_alone_when_there_is_no_time()
    {
        Assert.Equal("2026-09-06", DocumentDateFormat.Display(new DateOnly(2026, 9, 6), null));
        Assert.Equal("2026-09-06", DocumentDateFormat.Display("2026-09-06", null));
        Assert.Equal("2026-09-06", DocumentDateFormat.Display("2026-09-06", ""));
    }

    [Fact]
    public void Display_appends_the_utc_time_when_present()
    {
        Assert.Equal("2026-09-06 09:50 UTC", DocumentDateFormat.Display(new DateOnly(2026, 9, 6), new TimeOnly(9, 50)));
        Assert.Equal("2026-09-06 09:50 UTC", DocumentDateFormat.Display("2026-09-06", "09:50"));
    }

    [Theory]
    [InlineData("09:50", 9, 50)]
    [InlineData("9:50", 9, 50)]
    [InlineData("0950", 9, 50)]
    [InlineData("950", 9, 50)]
    [InlineData("9.50", 9, 50)]
    [InlineData("23:59", 23, 59)]
    [InlineData("0000", 0, 0)]
    public void TryParseTypedTime_accepts_the_obvious_spellings(string typed, int h, int m)
    {
        Assert.True(DocumentDateFormat.TryParseTypedTime(typed, out var time));
        Assert.Equal(new TimeOnly(h, m), time);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParseTypedTime_treats_blank_as_no_time(string? typed)
    {
        Assert.True(DocumentDateFormat.TryParseTypedTime(typed, out var time)); // blank is a SUCCESSFUL "no time"
        Assert.Null(time);
    }

    [Theory]
    [InlineData("24:00")]
    [InlineData("12:60")]
    [InlineData("2500")]
    [InlineData("nonsense")]
    [InlineData("9")]
    [InlineData("99999")]
    public void TryParseTypedTime_rejects_a_present_but_invalid_time(string typed)
    {
        Assert.False(DocumentDateFormat.TryParseTypedTime(typed, out var time));
        Assert.Null(time);
    }

    [Fact]
    public void FormatTime_round_trips_the_wire_form()
    {
        Assert.Equal("09:50", DocumentDateFormat.FormatTime(new TimeOnly(9, 50)));
        Assert.Null(DocumentDateFormat.FormatTime(null));
    }
}
