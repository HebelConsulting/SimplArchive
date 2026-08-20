using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The month grid (#660). 67 seeded concerts across five months is not a list: the flat list answers "what is
// coming up" and cannot answer "what does September look like", nor show that an act plays the same venue
// twice on one day.
//
// These assert the STRUCTURE rather than the styling, because the failures this class of change actually
// produces are structural — a grid that renders no cells, a header row that eats a week's height, a toggle
// that switches nothing. The desktop twin was rendered headlessly and three such bugs fell out of it, none of
// which the compiler or any existing test noticed.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-1")]
public class WebCalendarMonthTests
{
    private readonly SelfHostedAppFixture _app;

    public WebCalendarMonthTests(SelfHostedAppFixture app) => _app = app;

    private static async Task<IPage> CalendarAsync(SelfHostedAppFixture app)
    {
        var page = await Ui.LoginAsync(app);
        await page.Locator(".wb-tab[aria-label='Calendar']").First.ClickAsync();
        return page;
    }

    [Fact]
    public async Task The_tab_opens_on_the_month_grid_with_six_whole_weeks()
    {
        var page = await CalendarAsync(_app);

        // Opening on the grid is the decision: the tab exists to show a month.
        await Expect(page.Locator(".wb-cal-grid")).ToBeVisibleAsync();

        // Seven headings and forty-two cells — six weeks, always. A fixed count is what keeps the grid from
        // changing height as the user pages months, which otherwise moves the cell out from under the cursor.
        await Expect(page.Locator(".wb-cal-dow")).ToHaveCountAsync(7);
        await Expect(page.Locator(".wb-cal-day")).ToHaveCountAsync(42);
    }

    [Fact]
    public async Task The_weekday_header_is_not_as_tall_as_a_week()
    {
        var page = await CalendarAsync(_app);
        await Expect(page.Locator(".wb-cal-grid")).ToBeVisibleAsync();

        // grid-auto-rows sized the header as a seventh ROW, giving it a full week's height. It built clean and
        // looked plausible in isolation, which is exactly why this is measured rather than eyeballed.
        var headerHeight = await page.Locator(".wb-cal-dow").First.EvaluateAsync<double>(
            "e => e.getBoundingClientRect().height");
        var cellHeight = await page.Locator(".wb-cal-day").First.EvaluateAsync<double>(
            "e => e.getBoundingClientRect().height");

        Assert.True(
            headerHeight < cellHeight,
            $"the weekday header ({headerHeight}px) is at least as tall as a day cell ({cellHeight}px) — it is being sized as a week row.");
    }

    [Fact]
    public async Task The_toggle_switches_between_the_grid_and_the_list()
    {
        var page = await CalendarAsync(_app);
        await Expect(page.Locator(".wb-cal-grid")).ToBeVisibleAsync();

        await page.Locator("button[aria-label='List']").First.ClickAsync();
        await Expect(page.Locator(".wb-cal-grid")).Not.ToBeVisibleAsync();

        await page.Locator("button[aria-label='Month']").First.ClickAsync();
        await Expect(page.Locator(".wb-cal-grid")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Paging_a_month_changes_the_heading_and_keeps_the_grid_whole()
    {
        var page = await CalendarAsync(_app);
        var heading = page.Locator(".wb-cal-monthname");
        await Expect(heading).ToBeVisibleAsync();

        var before = await heading.InnerTextAsync();
        await page.Locator("button[aria-label='Next month']").First.ClickAsync();
        await Expect(heading).Not.ToHaveTextAsync(before);

        // Still six whole weeks: the grid must not reflow to fit whatever the next month happens to need.
        await Expect(page.Locator(".wb-cal-day")).ToHaveCountAsync(42);

        await page.Locator("button[aria-label='Today']").First.ClickAsync();
        await Expect(heading).ToHaveTextAsync(before);
    }

    // A multi-day entry occupies every day it covers, not just the one it starts on. Asserted through the
    // BROWSER as well as at the arithmetic (AppointmentCoverageTests) because the two are different failures:
    // the arithmetic can be right while the grid still renders one chip, which is what a start-day-only
    // EntriesOn() would do with a perfectly correct CoversDay sitting unused beside it.
    //
    // The seeded "Festival week" is the fixture — an ALL-DAY three-day run, so this also exercises iCalendar's
    // exclusive DTEND, which every other seeded entry is too short to reach.
    [Fact]
    public async Task A_multi_day_entry_occupies_every_day_it_covers()
    {
        var page = await CalendarAsync(_app);
        await Expect(page.Locator(".wb-cal-grid")).ToBeVisibleAsync();

        // The department's own calendar is not the personal default, so it starts unticked.
        var season = page.Locator(".mud-checkbox input[aria-label*='Season']").First;
        await Expect(season).ToBeVisibleAsync();
        await season.ClickAsync();
        await Expect(season).ToBeCheckedAsync();

        // The grid opens on TODAY's month while the seed is anchored to fixed dates, so page to the festival
        // rather than assuming the two coincide — otherwise this passes until September and then reports a
        // regression that is really a calendar turning over.
        await PageToAsync(page, new DateOnly(2026, 8, 1));

        // Three cells for one entry: 24, 25 and 26 August. A grid that placed the chip on its start day alone
        // would show exactly one, and the two missing days would read as "nothing is on".
        var chips = page.Locator(".wb-cal-entry").Filter(new LocatorFilterOptions { HasText = "Festival week" });
        await Expect(chips).ToHaveCountAsync(3);

        // ...of which the two after the first say they are a continuation rather than repeating a start.
        await Expect(page.Locator(".wb-cal-entry-cont").Filter(new LocatorFilterOptions { HasText = "Festival week" }))
            .ToHaveCountAsync(2);

        // The single-day entry in the same calendar is unaffected — the span must not leak into its neighbours.
        await Expect(page.Locator(".wb-cal-entry").Filter(new LocatorFilterOptions { HasText = "Site build-up" }))
            .ToHaveCountAsync(1);
    }

    /// <summary>Pages the grid to a given month by clicking, since the header carries no picker.</summary>
    /// <remarks>
    /// Bounded rather than a while-true: if the heading never arrives, a test that clicks for ever is a hung
    /// leg with no message, and the count it stops at is itself the diagnosis.
    /// </remarks>
    private static async Task PageToAsync(IPage page, DateOnly month)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var shift = ((month.Year - today.Year) * 12) + month.Month - today.Month;
        var button = shift < 0 ? "Previous month" : "Next month";

        Assert.True(
            Math.Abs(shift) <= 36,
            $"the seeded month is {Math.Abs(shift)} months from today — the seed has aged out of a plausible grid.");

        for (var i = 0; i < Math.Abs(shift); i++)
        {
            await page.Locator($"button[aria-label='{button}']").First.ClickAsync();
        }
    }
}
