using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The web half of the meeting-room booking surface (ADRs 0735/0743; the desktop's BookingDialog is the
// canonical shape, ADR 0511): selecting a room shows the Bookings… button — the `bookings` rel's presence
// is the affordance — and the dialog books, refuses a taken slot with the localized sentence, and cancels.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-2")]
public class WebBookingTests
{
    private readonly SelfHostedAppFixture _app;

    public WebBookingTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Book_a_room_hit_the_conflict_and_cancel()
    {
        var roomName = $"Room {Guid.NewGuid():N}"[..12];

        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));
        var repos = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories");
        var repoId = repos.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Demo Repository").GetProperty("id").GetGuid();
        var maskId = (await http.GetFromJsonAsync<JsonElement>("/api/masks")).GetProperty("masks").EnumerateArray()
            .First(m => m.GetProperty("name").GetString() == "Meeting room").GetProperty("id").GetGuid();
        (await http.PostAsJsonAsync($"/api/documents/{repoId}/children", new { name = roomName, maskId })).EnsureSuccessStatusCode();

        var page = await Ui.LoginAsync(_app);
        await page.GetByText("Demo Repository").First.ClickAsync();
        var row = page.Locator("[data-pane='list']").Locator(".wb-list-row").Filter(new() { HasText = roomName });
        await Expect(row).ToBeVisibleAsync();
        await row.ClickAsync();

        // The rel's presence is the affordance: the button exists because the room's resource advertised
        // `bookings` (ADR 0543) — a plain folder never shows it.
        await page.GetByRole(AriaRole.Button, new() { Name = "Bookings…", Exact = true }).ClickAsync();

        // The dialog's defaults (tomorrow, 09:00–10:00) are a valid slot: Book, and the row appears.
        var dialog = page.Locator(".mud-dialog");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Book", Exact = true }).ClickAsync();
        await Expect(dialog.GetByText("booked")).ToBeVisibleAsync();

        // The same slot again: refused with ApiErrorText's sentence — a rejection that names its reason,
        // never a bare failure (and never the server's English detail, issue #424 — this IS the client's).
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Book", Exact = true }).ClickAsync();
        await Expect(dialog.GetByText("This slot overlaps an existing booking of the same resource.")).ToBeVisibleAsync();

        // Cancel keeps the row as history: the status flips, the button goes.
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
        await Expect(dialog.GetByText("cancelled")).ToBeVisibleAsync();
        await Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true })).ToHaveCountAsync(0);
    }
}
