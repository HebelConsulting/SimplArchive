using System.Net.Http.Json;
using System.Text;

namespace SimplArchive.EndToEndTests;

// The intray must not lose a document's hour (#1304).
//
// WHAT WAS WRONG, and why nobody reported it. A document's date is a PAIR — DocumentDate plus the optional
// DocumentTime (ADR 0758) — and the intray read only the first: the staged draft was built from
// `version.DocumentDate.ToString("yyyy-MM-dd")`, the draft resource had no time member at all, and filing
// wrote `DocumentDate = now` with the time left null. So an item that arrived carrying an hour — an e-mail, a
// module-staged briefing, a scan somebody had dated — was silently stripped of it on the way in, and the form
// offered no way to put one back.
//
// It stayed invisible because "a date with no time" is a perfectly ordinary state: 22 writers in the solution
// legitimately create versions that way. Absence proves nothing, which is exactly why this asserts on a
// document that DEMONSTRABLY had an hour before it went through the door.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class IntrayKeepsTheHourTests
{
    private readonly E2EApiFactory _factory;

    public IntrayKeepsTheHourTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task A_document_copied_into_the_intray_keeps_the_hour_of_its_date()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"hour-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "u-1234", "Hour Owner", canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "u-1234"));

        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Hours {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var docName = $"metar-{Guid.NewGuid():N}";
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = docName })).GetProperty("id").GetGuid();

        var created = await TestJson.Post(owner, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        var versionId = created.GetProperty("id").GetGuid();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!,
                new ByteArrayContent(Encoding.UTF8.GetBytes("LSZH 200520Z 24006KT CAVOK")))).EnsureSuccessStatusCode();
        }

        await TestJson.Put(owner, $"/api/documents/{docId}/versions/{versionId}", new { });

        // 05:20 — not midnight and not a round hour, so the bug's output cannot pass as the fix's.
        (await owner.PutAsJsonAsync($"/api/documents/{docId}/versions/{versionId}/document-date",
            new { documentDate = "2026-09-20", documentTime = "05:20" })).EnsureSuccessStatusCode();

        var intray = await TestJson.Get(owner, "/api/intray");
        var fromDocument = intray.GetProperty("links").EnumerateArray()
            .First(l => l.GetProperty("rel").GetString() == "from-document").GetProperty("href").GetString()!;
        (await owner.PostAsJsonAsync(fromDocument, new { documentId = docId })).EnsureSuccessStatusCode();

        // THE ASSERTION: the staged draft carries the pair, not just the day.
        var draft = await TestJson.Get(owner, $"/api/intray/{docName + ".txt"}/mask");
        Assert.Equal("2026-09-20", draft.GetProperty("documentDate").GetString());
        Assert.Equal("05:20", draft.GetProperty("documentTime").GetString());
    }

    [Fact]
    public async Task Saving_a_draft_does_not_clear_a_staged_hour()
    {
        // The second half, and the one a client trips over rather than the server. The sidecar PUT is a FULL
        // REPLACEMENT: a caller that sends the draft back without the time clears it. Both clients were doing
        // exactly that before #1304, so editing the NAME of a staged item would have discarded its hour —
        // a loss with no error and no way to notice.
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"hour2-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "u-1234", "Hour Owner", canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "u-1234"));

        var itemName = $"staged-{Guid.NewGuid():N}.txt";
        var upload = await TestJson.Post(owner, "/api/intray", new { fileName = itemName });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(upload.GetProperty("uploadUrl").GetString()!,
                new ByteArrayContent(Encoding.UTF8.GetBytes("staged bytes")))).EnsureSuccessStatusCode();
        }

        (await owner.PutAsJsonAsync($"/api/intray/{itemName}/mask",
            new { name = "Weather", documentDate = "2026-09-20", documentTime = "05:20" })).EnsureSuccessStatusCode();

        var staged = await TestJson.Get(owner, $"/api/intray/{itemName}/mask");
        Assert.Equal("05:20", staged.GetProperty("documentTime").GetString());

        // Round-trip it the way a client does: read, change one unrelated field, write the whole draft back.
        (await owner.PutAsJsonAsync($"/api/intray/{itemName}/mask", new
        {
            name = "Weather renamed",
            documentDate = staged.GetProperty("documentDate").GetString(),
            documentTime = staged.GetProperty("documentTime").GetString(),
        })).EnsureSuccessStatusCode();

        var after = await TestJson.Get(owner, $"/api/intray/{itemName}/mask");
        Assert.Equal("Weather renamed", after.GetProperty("name").GetString());
        Assert.Equal("05:20", after.GetProperty("documentTime").GetString());
    }
}
