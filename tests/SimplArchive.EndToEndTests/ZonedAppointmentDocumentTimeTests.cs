namespace SimplArchive.EndToEndTests;

// A zoned appointment's DOCUMENT DATE/TIME must be the UTC instant, not the entry's wall clock (#1254).
//
// WHAT THIS IS ABOUT, and what it is NOT. The Appointment mask's Start/End index fields already carry an
// offset — AppointmentIndexTimeTests covers that and it is correct. The system fields are a different pair:
// DocumentVersion.DocumentDate + DocumentTime, documented as UTC, used for sorting, the document-date column
// and system-field search. `CalendarContactClassifier` fills them from `occurrence.DtStart.Value`, which in
// iCal.NET is the value in the entry's OWN TZID — a wall clock, not an instant.
//
// WHY IT NEEDS A TEST RATHER THAN A READING. A TimeOnly of 10:00 stored from Zurich and a TimeOnly of 10:00
// that is genuinely UTC are the same bits, so nothing downstream can tell them apart — not SaveChanges, not
// the database, not a reviewer looking at the row. The only moment the difference exists is the conversion,
// and the only way to see it is to put a zoned entry in and read the stored value out.
//
// And it is not hypothetical here: this project shipped an image with NO TZDATA, and every zoned calendar
// entry stored ~2 h out, silently, for as long as it took somebody to notice by hand — because every test host
// had tzdata and no test asked this question.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class ZonedAppointmentDocumentTimeTests
{
    private readonly E2EApiFactory _factory;

    public ZonedAppointmentDocumentTimeTests(E2EApiFactory factory) => _factory = factory;

    private static string Href(System.Text.Json.JsonElement resource, string rel) =>
        resource.GetProperty("links").EnumerateArray()
            .Single(l => l.GetProperty("rel").GetString() == rel).GetProperty("href").GetString()!;

    private async Task<(HttpClient Api, Guid CalendarId)> CalendarAsync()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"tzdoc-{Guid.NewGuid():N}@e2e.local";
        const string password = "tzdoc-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Zoned");
        var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var personalId = (await TestJson.Post(api, "/api/me/personal-repository", new { })).GetProperty("id").GetGuid();
        var calendarId = (await TestJson.Get(api, $"/api/documents/{personalId}/children"))
            .GetProperty("children").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "My Calendar").GetProperty("id").GetGuid();
        return (api, calendarId);
    }

    [Fact]
    public async Task A_zoned_appointment_stores_the_UTC_instant_as_its_document_time()
    {
        var (api, calendarId) = await CalendarAsync();
        using var _a = api;

        // 10:00 in Zurich on a date that is firmly in CEST (UTC+2), so the expected answer is 08:00 UTC and
        // the wrong answer is 10:00. A summer date deliberately: in winter the offset is +1, which would make
        // an off-by-one look like a near miss instead of a category error.
        var start = new DateTime(2026, 7, 15, 10, 0, 0, DateTimeKind.Unspecified);
        var created = await TestJson.Post(api, Href(await TestJson.Get(api, $"/api/documents/{calendarId}"), "appointments"), new
        {
            summary = "Zoned standup",
            start,
            end = start.AddHours(1),
            startTimeZoneId = "Europe/Zurich",
            endTimeZoneId = "Europe/Zurich",
        });

        var detail = await TestJson.Get(api, $"/api/documents/{created.GetProperty("id").GetGuid()}/detail");
        var documentTime = detail.GetProperty("documentTime").GetString();
        var documentDate = detail.GetProperty("documentDate").GetString();

        Assert.Equal("2026-07-15", documentDate);
        Assert.True(documentTime?.StartsWith("08:00", StringComparison.Ordinal) ?? false,
            $"documentTime is '{documentTime}'. A 10:00 Europe/Zurich appointment is 08:00 UTC, and "
            + "DocumentTime is documented as UTC — storing 10:00 means the entry's WALL CLOCK was written "
            + "into a field everything downstream reads as an instant (#1254). Nothing can detect that later: "
            + "10:00-from-Zurich and 10:00-UTC are the same bits.");
    }

    [Fact]
    public async Task A_zoned_appointment_near_midnight_moves_the_document_DATE_too()
    {
        var (api, calendarId) = await CalendarAsync();
        using var _a = api;

        // THE HALF THAT IS EASY TO MISS. The date and the time are ONE instant in two columns, so converting
        // the time without the date is not a smaller bug — it is a different and worse one, producing a row
        // whose two halves disagree. 00:30 Zurich on the 16th is 22:30 UTC on the FIFTEENTH.
        var start = new DateTime(2026, 7, 16, 0, 30, 0, DateTimeKind.Unspecified);
        var created = await TestJson.Post(api, Href(await TestJson.Get(api, $"/api/documents/{calendarId}"), "appointments"), new
        {
            summary = "Very early standup",
            start,
            end = start.AddHours(1),
            startTimeZoneId = "Europe/Zurich",
            endTimeZoneId = "Europe/Zurich",
        });

        var detail = await TestJson.Get(api, $"/api/documents/{created.GetProperty("id").GetGuid()}/detail");
        var documentDate = detail.GetProperty("documentDate").GetString();
        var documentTime = detail.GetProperty("documentTime").GetString();

        Assert.True(documentTime?.StartsWith("22:30", StringComparison.Ordinal) ?? false,
            $"documentTime is '{documentTime}'; 00:30 Europe/Zurich is 22:30 UTC.");
        Assert.Equal("2026-07-15", documentDate);
    }

    [Fact]
    public async Task A_floating_appointment_is_NOT_shifted()
    {
        var (api, calendarId) = await CalendarAsync();
        using var _a = api;

        // THE DELIBERATE EXCEPTION. A floating time has no zone by definition — it means "10:00 wherever you
        // are" — so stamping it with one is the bug, not the fix. This is the assertion that stops a
        // well-meaning "convert everything to UTC" sweep from breaking the one case that must not move.
        var start = new DateTime(2026, 7, 15, 10, 0, 0, DateTimeKind.Unspecified);
        var created = await TestJson.Post(api, Href(await TestJson.Get(api, $"/api/documents/{calendarId}"), "appointments"), new
        {
            summary = "Floating standup",
            start,
            end = start.AddHours(1),
        });

        var detail = await TestJson.Get(api, $"/api/documents/{created.GetProperty("id").GetGuid()}/detail");

        Assert.Equal("2026-07-15", detail.GetProperty("documentDate").GetString());
        Assert.True(detail.GetProperty("documentTime").GetString()?.StartsWith("10:00", StringComparison.Ordinal) ?? false,
            "A floating appointment must keep its wall clock — it has no zone to convert from.");
    }
}
