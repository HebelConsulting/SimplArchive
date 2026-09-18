using System.Text;

namespace SimplArchive.EndToEndTests;

// An e-mail's `Date:` header becomes the document's time-of-day (DocumentFinalizer, #1254).
//
// WHY THIS EXISTS (#1273). The line that turns a message header into a stored instant had NO test. Its
// neighbours were covered — DocumentTimeTests pins the column's round trip and the null case,
// ZonedAppointmentDocumentTimeTests pins the appointment producer — so the gap looked covered from a distance
// while the e-mail producer itself was guarded by nothing.
//
// It was found the long way round: somebody looked at the demo and asked why an e-mail had a date but no time.
// The answer turned out to be a stale demo volume rather than a defect (#1272) — but establishing that took a
// hand-built upload probe, which is precisely the test that should have existed.
//
// TWO ASSERTIONS, AND THE SECOND IS THE POINT. Asserting merely that a time is present cannot see a WRONG
// time, which is the failure this class of code actually has. So the fixture's header is deliberately written
// in `+0100`: the stored value must be the UTC one (07:14, not 08:14). A test using a `+0000` header would
// pass against code that dropped the offset entirely — it would assert that something happened, not that the
// right thing happened.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class EmailDocumentTimeTests
{
    private readonly E2EApiFactory _factory;

    public EmailDocumentTimeTests(E2EApiFactory factory) => _factory = factory;

    // 08:14 in +0100 is 07:14 UTC. Both numbers appear in the assertions below so a reader can see the
    // conversion being asserted rather than having to recompute it.
    private const string DateHeader = "Mon, 09 Feb 2026 08:14:00 +0100";

    [Fact]
    public async Task An_emails_Date_header_becomes_the_documents_date_and_UTC_time()
    {
        using var api = await AuthedClientAsync();
        var repoId = (await TestJson.Post(api, "/api/repositories", new { name = $"Mail time {Guid.NewGuid():N}" }))
            .GetProperty("id").GetGuid();

        var messageId = $"<{Guid.NewGuid():N}@e2e.local>";
        var finalized = await UploadEmlAsync(api, repoId, $"time-{Guid.NewGuid():N}", Rfc822(messageId));

        Assert.Equal("2026-02-09", finalized.GetProperty("documentDate").GetString());

        // The value, not its presence — and the UTC one, not the header's wall clock.
        Assert.Equal("07:14", finalized.GetProperty("documentTime").GetString());
    }

    [Fact]
    public async Task A_document_that_carries_no_instant_keeps_a_null_time()
    {
        using var api = await AuthedClientAsync();
        var repoId = (await TestJson.Post(api, "/api/repositories", new { name = $"Flat date {Guid.NewGuid():N}" }))
            .GetProperty("id").GetGuid();

        var docId = (await TestJson.Post(api, $"/api/documents/{repoId}/children", new { name = $"flat-{Guid.NewGuid():N}" }))
            .GetProperty("id").GetGuid();
        var created = await TestJson.Post(api, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        var versionId = created.GetProperty("id").GetGuid();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!,
                new ByteArrayContent(Encoding.UTF8.GetBytes("no instant here")))).EnsureSuccessStatusCode();
        }

        var finalized = await TestJson.Put(api, $"/api/documents/{docId}/versions/{versionId}", new { });

        // Null is a REAL answer, not a gap waiting to be filled (DocumentInstant): a document dated to the day
        // never was an instant, and giving it a notional midnight would make it drift a day west of the
        // server. This case exists so a later "helpful" default cannot be added without failing a test.
        var time = finalized.GetProperty("documentTime");
        Assert.True(time.ValueKind == System.Text.Json.JsonValueKind.Null,
            $"A .txt carries no instant, so its time must stay null — got '{time}'. See DocumentInstant: a "
            + "notional midnight makes a date-only document change day between time zones.");
    }

    private async Task<HttpClient> AuthedClientAsync()
    {
        var email = $"mailtime-{Guid.NewGuid():N}@e2e.local";
        const string password = "mailtimepw1234";
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        await _factory.SeedUserAsync(tenantId, email, password, "Mail Time", canManageRepositories: true);
        return _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));
    }

    private static string Rfc822(string messageId) =>
        "From: billing@citytransit.example\r\n"
        + "To: someone@e2e.local\r\n"
        + $"Subject: Ticket invoice {Guid.NewGuid():N}\r\n"
        + $"Date: {DateHeader}\r\n"
        + $"Message-ID: {messageId}\r\n"
        + "MIME-Version: 1.0\r\n"
        + "Content-Type: text/plain; charset=utf-8\r\n\r\n"
        + "Body.\r\n";

    private static async Task<System.Text.Json.JsonElement> UploadEmlAsync(
        HttpClient api, Guid repoId, string name, string rfc822)
    {
        var docId = (await TestJson.Post(api, $"/api/documents/{repoId}/children", new { name }))
            .GetProperty("id").GetGuid();
        var created = await TestJson.Post(api, $"/api/documents/{docId}/versions", new { fileExtension = ".eml" });
        var versionId = created.GetProperty("id").GetGuid();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!,
                new ByteArrayContent(Encoding.UTF8.GetBytes(rfc822)))).EnsureSuccessStatusCode();
        }

        return await TestJson.Put(api, $"/api/documents/{docId}/versions/{versionId}", new { });
    }
}
