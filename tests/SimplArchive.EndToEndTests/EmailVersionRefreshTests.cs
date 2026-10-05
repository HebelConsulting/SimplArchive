using System.Net.Http.Json;
using System.Text;

namespace SimplArchive.EndToEndTests;

// A new version of an .eml RE-EXTRACTS its index fields (#1466, ADR 0850).
//
// ADR 0744 gave the edit paths a refresh and derived its refreshable set from DavCollectionKinds.All — so it
// covered .ics and .vcf and structurally excluded everything else. An e-mail therefore SERVED the new version
// while INDEXING the first one's From, Subject and Date. Nothing failed; the document simply described a
// different message than it delivered.
//
// REFUSING the second version was tried first and was WRONG: a second version of an e-mail is a documented
// feature — an IMAP re-append of the same Message-ID files a new version rather than a duplicate document
// (#782) and an .eml version pair is a compared surface. Two named tests said so by failing.
//
// THE ASSERTIONS ARE THE NEW VALUES, not their presence. Asserting that a Subject exists cannot see the WRONG
// subject, which is the whole defect — so both messages' values appear below and the test says which must win.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class EmailVersionRefreshTests
{
    private readonly E2EApiFactory _factory;

    public EmailVersionRefreshTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Filing_a_different_message_as_version_2_re_extracts_its_headers()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"refresh-{Guid.NewGuid():N}@e2e.local";
        const string password = "refresh-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Refresh", canManageRepositories: true, isTenantAdmin: true);
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var repoId = (await TestJson.Post(api, "/api/repositories", new { name = $"refresh-{Guid.NewGuid():N}" }))
            .GetProperty("id").GetGuid();

        var docId = await FileEmlAsync(api, repoId, parentId: null,
            from: "first@e2e.local", subject: "The first message", date: "Tue, 15 Jul 2026 08:14:00 +0100");

        Assert.Equal("first@e2e.local", await FieldAsync(api, docId, "From"));
        Assert.Equal("The first message", await FieldAsync(api, docId, "Subject"));
        // The full instant with the SENDER's offset — the Date field kept only the day before 2026-10-05.
        Assert.Equal("2026-07-15T08:14:00+01:00", await FieldAsync(api, docId, "Date"));

        // A DIFFERENT message, filed as version 2 of the same document.
        await FileEmlAsync(api, repoId, parentId: docId,
            from: "second@e2e.local", subject: "A later message", date: "Thu, 20 Aug 2026 09:30:00 +0000");

        // Every one of these was the FIRST message's value before the fix.
        Assert.Equal("second@e2e.local", await FieldAsync(api, docId, "From"));
        Assert.Equal("A later message", await FieldAsync(api, docId, "Subject"));
        Assert.Equal("2026-08-20T09:30:00+00:00", await FieldAsync(api, docId, "Date"));
    }

    [Fact]
    public async Task A_header_that_DISAPPEARED_between_versions_does_not_linger()
    {
        // The half a plain re-write misses: the refresh REPLACES the fields it owns, so a Cc removed between
        // versions is gone rather than left behind — and two versions' values never accumulate on one document.
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"refresh2-{Guid.NewGuid():N}@e2e.local";
        const string password = "refresh2-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Refresh2", canManageRepositories: true, isTenantAdmin: true);
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var repoId = (await TestJson.Post(api, "/api/repositories", new { name = $"refresh2-{Guid.NewGuid():N}" }))
            .GetProperty("id").GetGuid();

        var docId = await FileEmlAsync(api, repoId, parentId: null,
            from: "a@e2e.local", subject: $"With a Cc {Guid.NewGuid():N}", date: "Tue, 15 Jul 2026 08:14:00 +0100",
            cc: "watcher@e2e.local");

        Assert.Equal("watcher@e2e.local", await FieldAsync(api, docId, "Cc"));

        await FileEmlAsync(api, repoId, parentId: docId,
            from: "a@e2e.local", subject: $"No Cc now {Guid.NewGuid():N}", date: "Tue, 15 Jul 2026 08:14:00 +0100");

        Assert.Null(await FieldAsync(api, docId, "Cc"));
    }

    /// <summary>
    /// One index field's first value, or null when the field carries none.
    /// </summary>
    /// <remarks>
    /// The listing names a field <c>fieldName</c> and carries <c>values</c> as an ARRAY — a field may hold
    /// several (#703) — so a cleared field is present with an empty array rather than absent. Both shapes
    /// therefore have to answer null, which is what the Cc assertion depends on.
    /// </remarks>
    private static async Task<string?> FieldAsync(HttpClient api, Guid documentId, string field)
    {
        var data = await TestJson.Get(api, $"/api/documents/{documentId}/index-data");
        foreach (var entry in data.GetProperty("fields").EnumerateArray())
        {
            if (entry.GetProperty("fieldName").GetString() != field)
            {
                continue;
            }

            foreach (var value in entry.GetProperty("values").EnumerateArray())
            {
                var text = value.GetString();
                return string.IsNullOrEmpty(text) ? null : text;
            }

            return null;
        }

        return null;
    }

    /// <summary>Files an .eml — a new document when <paramref name="parentId"/> is null, else a new VERSION of it.</summary>
    private static async Task<Guid> FileEmlAsync(
        HttpClient api, Guid repoId, Guid? parentId, string from, string subject, string date, string? cc = null)
    {
        var rfc822 = $"From: {from}\r\n"
            + "To: someone@e2e.local\r\n"
            + (cc is null ? string.Empty : $"Cc: {cc}\r\n")
            + $"Subject: {subject}\r\n"
            + $"Date: {date}\r\n"
            + $"Message-ID: <{Guid.NewGuid():N}@e2e.local>\r\n"
            + "MIME-Version: 1.0\r\n"
            + "Content-Type: text/plain; charset=utf-8\r\n\r\n"
            + "Body.\r\n";

        var docId = parentId ?? (await TestJson.Post(api, $"/api/documents/{repoId}/children",
            new { name = $"mail-{Guid.NewGuid():N}" })).GetProperty("id").GetGuid();

        var created = await TestJson.Post(api, $"/api/documents/{docId}/versions", new { fileExtension = ".eml" });
        var versionId = created.GetProperty("id").GetGuid();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!,
                new ByteArrayContent(Encoding.UTF8.GetBytes(rfc822)))).EnsureSuccessStatusCode();
        }

        await TestJson.Put(api, $"/api/documents/{docId}/versions/{versionId}", new { });
        return docId;
    }
}
