using System.Globalization;
using SimplArchive.Presentation;

namespace SimplArchive.UnitTests;

// The shared DateTime-index-value arithmetic (ADR 0650's rule: one answer, two renderings). The pane bug
// this fixes: the wire value ("2026-09-04T12:30:00+00:00") was shown raw, which reads as a date with
// debris, and the edit form offered no way to state the time at all.
public class IndexInstantTests
{
    [Fact]
    public void Display_shows_the_local_wall_clock_of_the_instant()
    {
        var display = IndexInstant.Display("2026-09-04T12:30:00+00:00");

        // Zone-safe: the expected rendering is computed through the same local conversion the user's
        // machine applies — what the test pins is the FORMAT (date and minutes, no offset debris) and
        // that it names the same instant.
        var expected = DateTimeOffset.Parse("2026-09-04T12:30:00+00:00", CultureInfo.InvariantCulture)
            .ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        Assert.Equal(expected, display);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}$", display);
    }

    [Fact]
    public void A_value_that_is_not_an_instant_is_shown_as_it_stands()
    {
        // Display never invents a value — a malformed one is at least visible as itself.
        Assert.Equal("not a moment", IndexInstant.Display("not a moment"));
    }

    [Fact]
    public void Split_and_compose_round_trip_the_instant()
    {
        var (date, time) = IndexInstant.Split("2026-09-04T12:30:00+00:00");
        Assert.NotNull(date);
        Assert.NotNull(time);

        var composed = IndexInstant.Compose(date, time);
        Assert.NotNull(composed);

        // The composed value carries the LOCAL offset, but it must name the SAME moment that went in.
        Assert.Equal(
            DateTimeOffset.Parse("2026-09-04T12:30:00+00:00", CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(composed, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void A_date_without_a_time_composes_to_local_midnight_and_no_date_to_nothing()
    {
        Assert.Null(IndexInstant.Compose(null, new TimeSpan(9, 0, 0)));

        var composed = IndexInstant.Compose(new DateTime(2026, 9, 4), null);
        Assert.NotNull(composed);
        Assert.StartsWith("2026-09-04T00:00:00", composed);
    }
}
