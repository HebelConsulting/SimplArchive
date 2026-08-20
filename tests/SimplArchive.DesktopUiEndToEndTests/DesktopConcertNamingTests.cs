using System.Text.RegularExpressions;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// What the seeded concerts are CALLED (#664). Every name used to begin `yyyy-MM-dd`, which put an ISO date
// where a reader looks for a venue — and a month cell is narrow, so two shows at one venue on one day read as
// "Shalin Liu P…" and "2026-09-01…", the second identifying nothing at all.
//
// A sibling-name collision was being resolved in the one place the user reads. These pin both halves of the
// fix: the names stay unique (a collision fails the demo seed outright, so this is load-bearing), and the
// uniqueness is bought with the time rather than with a date nobody needed.
[Collection(UiCollection.Name)]
public class DesktopConcertNamingTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopConcertNamingTests(SelfHostedAppFixture app) => _app = app;

    private static readonly Regex LeadingIsoDate = new(@"^\d{4}-\d{2}-\d{2}", RegexOptions.Compiled);

    private async Task<List<(string Collection, DavEntry Entry)>> ConcertsAsync()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var found = new List<(string, DavEntry)>();
        foreach (var calendar in await api.DavCollections.ListAsync("calendar"))
        {
            if (calendar.Links.TryGetValue("appointments", out var href))
            {
                foreach (var entry in await api.DavCollections.ListEntriesAsync(href))
                {
                    found.Add((calendar.DisplayName, entry));
                }
            }
        }

        return found;
    }

    [Fact]
    public async Task No_concert_is_named_after_a_date()
    {
        var concerts = await ConcertsAsync();

        // Guard the guard: if the seed ever stops producing concerts this test would pass having read nothing,
        // which is precisely how a naming regression would slip back in unnoticed.
        Assert.True(concerts.Count > 10, $"only {concerts.Count} appointments came back — the events seed is not present, so this proves nothing.");

        var dated = concerts.Where(c => LeadingIsoDate.IsMatch(c.Entry.Name)).Select(c => c.Entry.Name).ToList();
        Assert.True(
            dated.Count == 0,
            "these concerts are named after their date rather than their venue, which is what a narrow month cell "
            + $"truncates to an ISO prefix identifying nothing:\n  {string.Join("\n  ", dated)}");
    }

    [Fact]
    public async Task Concert_names_are_unique_within_their_calendar()
    {
        var concerts = await ConcertsAsync();
        Assert.True(concerts.Count > 10, $"only {concerts.Count} appointments came back — the events seed is not present, so this proves nothing.");

        var clashes = concerts
            .GroupBy(c => (c.Collection, c.Entry.Name))
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key.Collection}: {g.Key.Name} ×{g.Count()}")
            .ToList();

        Assert.True(
            clashes.Count == 0,
            "two concerts in one calendar share a name. The seed writes these into one folder, where a duplicate "
            + $"is a sibling-name conflict that fails provisioning outright:\n  {string.Join("\n  ", clashes)}");
    }

    // The case that motivated the change: the same venue twice on one day. The two must be tellable apart, and
    // by their time — which is the thing a reader would actually say out loud to distinguish them.
    [Fact]
    public async Task Two_shows_at_one_venue_on_one_day_are_told_apart_by_their_time()
    {
        var concerts = await ConcertsAsync();

        var sameDay = concerts
            .Where(c => c.Entry is { AllDay: false, Start: not null })
            .GroupBy(c => (c.Collection, Day: c.Entry.Start![..10], Venue: c.Entry.Location))
            .FirstOrDefault(g => g.Count() > 1);

        Assert.NotNull(sameDay);

        var names = sameDay!.Select(c => c.Entry.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());

        // Each carries its own start time, so the qualifier is the time rather than a date the cell already knows.
        Assert.All(sameDay, c =>
        {
            var time = DateTimeOffset.Parse(c.Entry.Start!, System.Globalization.CultureInfo.InvariantCulture).ToString("HH:mm");
            Assert.True(
                c.Entry.Name.Contains(time, StringComparison.Ordinal),
                $"'{c.Entry.Name}' shares a venue and a day with a sibling but does not carry its {time} start to say which it is.");
        });
    }
}
