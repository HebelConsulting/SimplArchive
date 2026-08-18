using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Xml.Linq;

namespace SimplArchive.EndToEndTests;

// The structured appointment editor's API (#564, ADR 0631): GET the six modelled fields, PUT them back merged
// into the stored iCalendar entry. Driven end to end because what is most likely to break is invisible to a
// unit test — the entry is read from and written to real object storage, as a new version, and re-classified.
//
// The appointment is created the way a real one arrives: a CalDAV client PUTs it. A fixture written by the test
// would only carry properties the test author thought of, and the whole point of the merge is what happens to
// the ones nobody thought of.
[Collection(E2ECollection.Name)]
public class AppointmentEndpointTests
{
    private readonly E2EApiFactory _factory;

    public AppointmentEndpointTests(E2EApiFactory factory) => _factory = factory;

    // Fields we model, plus ones we do not (CATEGORIES, STATUS, TRANSP, X-*), an ATTENDEE, and two VALARMs —
    // the reminder is the property ADR 0631 decision 4 is really about.
    private static string Entry(string uid) =>
        "BEGIN:VCALENDAR\r\n"
        + "VERSION:2.0\r\n"
        + "PRODID:-//Some Phone//Calendar 1.0//EN\r\n"
        + "BEGIN:VEVENT\r\n"
        + $"UID:{uid}\r\n"
        + "DTSTAMP:20260801T090000Z\r\n"
        + "DTSTART;TZID=Europe/Zurich:20260901T140000\r\n"
        + "DTEND;TZID=Europe/Zurich:20260901T150000\r\n"
        + "SUMMARY:Weekly sync\r\n"
        + "LOCATION:Room 3\r\n"
        + "DESCRIPTION:Agenda in the shared folder.\r\n"
        + "RRULE:FREQ=WEEKLY;BYDAY=TU\r\n"
        + "CATEGORIES:Work,Recurring\r\n"
        + "STATUS:CONFIRMED\r\n"
        + "TRANSP:OPAQUE\r\n"
        + "X-MY-CUSTOM-FLAG:keep-me\r\n"
        + "ATTENDEE;CN=Tom Fischer;PARTSTAT=ACCEPTED:mailto:tom@example.test\r\n"
        + "BEGIN:VALARM\r\nACTION:DISPLAY\r\nTRIGGER:-PT15M\r\nDESCRIPTION:Reminder\r\nEND:VALARM\r\n"
        + "BEGIN:VALARM\r\nACTION:AUDIO\r\nTRIGGER:-PT5M\r\nEND:VALARM\r\n"
        + "END:VEVENT\r\n"
        + "END:VCALENDAR\r\n";

    /// <summary>A user, an appointment filed by a CalDAV PUT, and the document id it landed as.</summary>
    private async Task<(HttpClient Api, HttpClient Dav, AuthenticationHeaderValue Auth, Guid DocumentId, string ItemHref)> AppointmentAsync()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"appt-{Guid.NewGuid():N}@e2e.local";
        const string password = "appt-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Appointment Editor");
        var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));
        await TestJson.Post(api, "/api/me/personal-repository", new { });

        var generated = await TestJson.Post(api, "/api/me/webdav-password", new { });
        var auth = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{generated.GetProperty("password").GetString()}")));
        var dav = _factory.CreateClient();

        // The collection href is DISCOVERED via PROPFIND, never composed — the server owns its URL space.
        using var probe = new HttpRequestMessage(new HttpMethod("PROPFIND"), "/caldav/calendars/")
        {
            Headers = { Authorization = auth },
        };
        probe.Headers.TryAddWithoutValidation("Depth", "1");
        var listing = XDocument.Parse(await (await dav.SendAsync(probe)).Content.ReadAsStringAsync());
        XNamespace davNs = "DAV:";
        var collectionHref = listing.Descendants(davNs + "response")
            .Single(r => r.Descendants(davNs + "displayname").Any(d => d.Value.EndsWith("My Calendar", StringComparison.Ordinal)))
            .Element(davNs + "href")!.Value;

        var uid = $"uid-{Guid.NewGuid():N}";
        using var put = new HttpRequestMessage(HttpMethod.Put, $"{collectionHref}{uid}.ics")
        {
            Content = new StringContent(Entry(uid), Encoding.UTF8, "text/calendar"),
            Headers = { Authorization = auth },
        };
        var response = await dav.SendAsync(put);
        Assert.True(response.StatusCode is HttpStatusCode.Created or HttpStatusCode.NoContent or HttpStatusCode.OK,
            $"CalDAV PUT returned {(int)response.StatusCode}");

        var personal = await TestJson.Post(api, "/api/me/personal-repository", new { });
        var documentId = await FindAppointmentAsync(api, personal.GetProperty("id").GetGuid());
        return (api, dav, auth, documentId, $"{collectionHref}{uid}.ics");
    }

    /// <summary>Walks the personal space for the appointment the DAV PUT filed.</summary>
    private static async Task<Guid> FindAppointmentAsync(HttpClient api, Guid rootId)
    {
        var queue = new Queue<Guid>();
        queue.Enqueue(rootId);
        while (queue.Count > 0)
        {
            var children = await TestJson.Get(api, $"/api/documents/{queue.Dequeue()}/children?limit=200");
            foreach (var child in children.GetProperty("children").EnumerateArray())
            {
                var id = child.GetProperty("id").GetGuid();
                if (child.GetProperty("name").GetString()?.Contains("Weekly sync", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return id;
                }

                queue.Enqueue(id);
            }
        }

        Assert.Fail("No appointment document found");
        return Guid.Empty;
    }

    [Fact]
    public async Task The_entry_reads_back_as_structured_fields_in_its_own_zone()
    {
        var (api, _, _, documentId, _) = await AppointmentAsync();
        using var _a = api;

        var appointment = await TestJson.Get(api, $"/api/documents/{documentId}/appointment");

        Assert.Equal("Weekly sync", appointment.GetProperty("summary").GetString());
        Assert.Equal("Room 3", appointment.GetProperty("location").GetString());
        Assert.Equal("Agenda in the shared folder.", appointment.GetProperty("description").GetString());
        Assert.Equal("FREQ=WEEKLY;BYDAY=TU", appointment.GetProperty("recurrenceRule").GetString());

        // The appointment's OWN zone, and the wall clock as written — 14:00, not converted for the reader
        // (ADR 0631 decision 5).
        Assert.Equal("Europe/Zurich", appointment.GetProperty("timeZoneId").GetString());
        Assert.StartsWith("2026-09-01T14:00:00", appointment.GetProperty("start").GetString());
        Assert.False(appointment.GetProperty("isAllDay").GetBoolean());

        // Shown, never editable (decision 3) — the form displays who replied and how.
        Assert.Equal("Tom Fischer", appointment.GetProperty("attendees")[0].GetProperty("name").GetString());
        Assert.Equal("tom@example.test", appointment.GetProperty("attendees")[0].GetProperty("address").GetString());
        Assert.Equal("ACCEPTED", appointment.GetProperty("attendees")[0].GetProperty("status").GetString());

        // The form says a reminder is set without implying it can be changed here.
        Assert.Equal(2, appointment.GetProperty("reminderCount").GetInt32());
        Assert.True(appointment.GetProperty("canEdit").GetBoolean());
    }

    [Fact]
    public async Task Saving_an_edit_preserves_the_reminder_and_everything_else_unmodelled()
    {
        // The reason the composer exists, proved through the real storage round trip: a user fixes the title
        // here, and the reminder they set on their phone must still ring afterwards.
        var (api, dav, auth, documentId, itemHref) = await AppointmentAsync();
        using var _a = api;
        using var _d = dav;

        var get = await api.GetAsync($"/api/documents/{documentId}/appointment");
        var appointment = await TestJson.Get(api, $"/api/documents/{documentId}/appointment");

        using var put = new HttpRequestMessage(HttpMethod.Put, $"/api/documents/{documentId}/appointment")
        {
            Content = JsonContent.Create(new
            {
                summary = "Weekly sync (renamed)",
                start = appointment.GetProperty("start").GetString(),
                end = appointment.GetProperty("end").GetString(),
                isAllDay = false,
                timeZoneId = "Europe/Zurich",
                location = "Room 4",
                description = "Agenda in the shared folder.",
                recurrenceRule = "FREQ=WEEKLY;BYDAY=TU",
            }),
        };
        put.Headers.TryAddWithoutValidation("If-Match", get.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.NoContent, (await api.SendAsync(put)).StatusCode);

        // Read the STORED ENTRY back over CalDAV — the bytes another client would sync, not our own projection.
        var raw = Unfold(await RawEntryAsync(dav, auth, itemHref));

        Assert.Contains("SUMMARY:Weekly sync (renamed)", raw);
        Assert.Contains("LOCATION:Room 4", raw);

        // Both reminders, intact.
        Assert.Equal(2, raw.Split("BEGIN:VALARM").Length - 1);
        Assert.Contains("TRIGGER:-PT15M", raw);
        Assert.Contains("TRIGGER:-PT5M", raw);

        // …and everything else the form does not model.
        Assert.Contains("CATEGORIES:Work,Recurring", raw);
        Assert.Contains("STATUS:CONFIRMED", raw);
        Assert.Contains("TRANSP:OPAQUE", raw);
        Assert.Contains("X-MY-CUSTOM-FLAG:keep-me", raw);
        Assert.Contains("PARTSTAT=ACCEPTED", raw);

        // The zone is preserved rather than converted — this is what stops a weekly meeting drifting an hour
        // across a daylight-saving change.
        Assert.Contains("DTSTART;TZID=Europe/Zurich:20260901T140000", raw);
    }

    [Fact]
    public async Task The_uid_survives_a_save_so_the_appointment_does_not_fork_on_the_next_sync()
    {
        // The UID is the correlation key a DAV client matches on, so it is taken from the stored entry by
        // FIELD NAME rather than from whichever index value the database happens to return first. Reading it
        // unfiltered is a real defect that shipped on the contact side and is fixed alongside this.
        var (api, dav, auth, documentId, itemHref) = await AppointmentAsync();
        using var _a = api;
        using var _d = dav;

        var uidBefore = UidOf(await RawEntryAsync(dav, auth, itemHref));
        Assert.StartsWith("uid-", uidBefore);

        var get = await api.GetAsync($"/api/documents/{documentId}/appointment");
        var appointment = await TestJson.Get(api, $"/api/documents/{documentId}/appointment");
        using var put = new HttpRequestMessage(HttpMethod.Put, $"/api/documents/{documentId}/appointment")
        {
            Content = JsonContent.Create(new
            {
                summary = "Edited once",
                start = appointment.GetProperty("start").GetString(),
                end = appointment.GetProperty("end").GetString(),
                timeZoneId = "Europe/Zurich",
                location = "Room 3",
            }),
        };
        put.Headers.TryAddWithoutValidation("If-Match", get.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.NoContent, (await api.SendAsync(put)).StatusCode);

        Assert.Equal(uidBefore, UidOf(await RawEntryAsync(dav, auth, itemHref)));
    }

    [Fact]
    public async Task A_save_writes_a_new_version_rather_than_mutating_the_stored_object()
    {
        var (api, _, _, documentId, _) = await AppointmentAsync();
        using var _a = api;

        var before = (await TestJson.Get(api, $"/api/documents/{documentId}/versions")).GetProperty("versions").GetArrayLength();

        var get = await api.GetAsync($"/api/documents/{documentId}/appointment");
        var appointment = await TestJson.Get(api, $"/api/documents/{documentId}/appointment");
        using var put = new HttpRequestMessage(HttpMethod.Put, $"/api/documents/{documentId}/appointment")
        {
            Content = JsonContent.Create(new
            {
                summary = "Edited",
                start = appointment.GetProperty("start").GetString(),
                end = appointment.GetProperty("end").GetString(),
                timeZoneId = "Europe/Zurich",
            }),
        };
        put.Headers.TryAddWithoutValidation("If-Match", get.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.NoContent, (await api.SendAsync(put)).StatusCode);

        var after = (await TestJson.Get(api, $"/api/documents/{documentId}/versions")).GetProperty("versions").GetArrayLength();
        Assert.Equal(before + 1, after);
    }

    [Fact]
    public async Task A_stale_If_Match_is_refused_and_a_missing_one_is_required()
    {
        var (api, _, _, documentId, _) = await AppointmentAsync();
        using var _a = api;

        var body = new { summary = "Whatever", timeZoneId = "Europe/Zurich" };

        using var noMatch = new HttpRequestMessage(HttpMethod.Put, $"/api/documents/{documentId}/appointment")
        {
            Content = JsonContent.Create(body),
        };
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await api.SendAsync(noMatch)).StatusCode);

        using var stale = new HttpRequestMessage(HttpMethod.Put, $"/api/documents/{documentId}/appointment")
        {
            Content = JsonContent.Create(body),
        };
        stale.Headers.TryAddWithoutValidation("If-Match", $"\"{Guid.NewGuid()}\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await api.SendAsync(stale)).StatusCode);
    }

    [Fact]
    public async Task A_document_that_is_not_an_appointment_has_none()
    {
        var (api, _, _, _, _) = await AppointmentAsync();
        using var _a = api;

        var personal = await TestJson.Post(api, "/api/me/personal-repository", new { });
        var folder = await TestJson.Post(api, $"/api/documents/{personal.GetProperty("id").GetGuid()}/children",
            new { name = $"Plain {Guid.NewGuid():N}"[..14] });

        var response = await api.GetAsync($"/api/documents/{folder.GetProperty("id").GetGuid()}/appointment");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static string Unfold(string ics) => ics.Replace("\r\n ", string.Empty).Replace("\r\n\t", string.Empty);

    private static string UidOf(string ics) =>
        Unfold(ics).Split("\r\n").First(l => l.StartsWith("UID:", StringComparison.Ordinal))["UID:".Length..];

    /// <summary>
    /// The stored entry as ANOTHER CLIENT would sync it — fetched back over CalDAV rather than through our own
    /// structured projection, which would only prove the projection is self-consistent.
    /// </summary>
    private static async Task<string> RawEntryAsync(HttpClient dav, AuthenticationHeaderValue auth, string itemHref)
    {
        using var get = new HttpRequestMessage(HttpMethod.Get, itemHref) { Headers = { Authorization = auth } };
        var response = await dav.SendAsync(get);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }
}
