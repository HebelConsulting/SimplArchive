using System.Globalization;
using SimplArchive.Presentation;

namespace SimplArchive.UnitTests;

// The shared DateTime-index-value arithmetic (ADR 0650's rule: one answer, two renderings). The pane bug
// this fixes: the wire value ("2026-09-04T12:30:00+00:00") was shown raw, which reads as a date with
// debris, and the edit form offered no way to state the time at all.
public class IndexInstantTests
{
    // A FIXED zone, not the machine's. The previous version of these tests computed every expectation by
    // calling ToLocalTime() itself — "zone-safe", and therefore unable to fail when the production code used
    // the device's zone instead of the viewer's, which is exactly the defect that shipped (#1315). A test
    // that performs the same conversion as the code under test is asserting that addition is addition.
    private static readonly TimeZoneInfo Zurich =
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "W. Europe Standard Time" : "Europe/Zurich");

    [Fact]
    public void Display_shows_the_instant_on_the_viewers_clock()
    {
        // 12:30 UTC in September is 14:30 in Zurich (+02:00). Stated as a literal, so this fails if the
        // conversion is dropped, uses the device zone, or picks the wrong offset for the date.
        Assert.Equal("2026-09-04 14:30", IndexInstant.Display("2026-09-04T12:30:00+00:00", Zurich));
        Assert.Equal("2026-09-04 12:30", IndexInstant.Display("2026-09-04T12:30:00+00:00", TimeZoneInfo.Utc));
    }

    [Fact]
    public void A_value_that_is_not_an_instant_is_shown_as_it_stands()
    {
        // Display never invents a value — a malformed one is at least visible as itself.
        Assert.Equal("not a moment", IndexInstant.Display("not a moment", Zurich));
    }

    [Fact]
    public void Split_and_compose_round_trip_the_instant()
    {
        var (date, time) = IndexInstant.Split("2026-09-04T12:30:00+00:00", Zurich);
        Assert.NotNull(date);
        Assert.NotNull(time);

        // The pair the pickers hold is the VIEWER's wall clock, not UTC and not the device's.
        Assert.Equal(new DateTime(2026, 9, 4), date);
        Assert.Equal(new TimeSpan(14, 30, 0), time);

        var composed = IndexInstant.Compose(date, time, Zurich);
        Assert.NotNull(composed);

        // The composed value carries the VIEWER's offset, but it must name the SAME moment that went in.
        // This is the half that makes the pair safe: showing one clock and saving another would store a
        // value hours from what the user read, with neither half looking wrong on its own.
        Assert.Equal(
            DateTimeOffset.Parse("2026-09-04T12:30:00+00:00", CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(composed, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void A_date_without_a_time_composes_to_midnight_on_the_viewers_clock()
    {
        Assert.Null(IndexInstant.Compose(null, new TimeSpan(9, 0, 0), Zurich));

        var composed = IndexInstant.Compose(new DateTime(2026, 9, 4), null, Zurich);
        Assert.NotNull(composed);
        Assert.StartsWith("2026-09-04T00:00:00", composed);
        Assert.EndsWith("+02:00", composed);
    }
}
