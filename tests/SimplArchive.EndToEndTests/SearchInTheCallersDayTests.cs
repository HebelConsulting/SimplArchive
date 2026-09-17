using System.Net.Http.Json;
using System.Text;

namespace SimplArchive.EndToEndTests;

// A search for "the 16th" means the CALLER'S 16th, not the server's (#1254).
//
// WHAT GOES WRONG WITHOUT THIS. The document date+time pair is stored in UTC, and the filter used to compare
// the date column verbatim. So a document stamped 22:30 UTC on the 15th — which is 00:30 on the 16th for
// anyone in Zurich, and is exactly what the clients now DISPLAY — was invisible to a search for the 16th and
// turned up under the 15th, a day the user never saw written anywhere. One document, one day a year per hour
// of offset, and no error: the search simply returns one row fewer than it should.
//
// WHY IT NEEDS AN E2E TEST. Three parts have to agree for this to work, and each is fine on its own: the
// INDEXER has to write `documentInstant`, the QUERY has to translate a local day into a UTC range, and the
// CALLER'S zone has to reach the query (a header, or the stored preference). A unit test of the range
// arithmetic passes whether or not the field is indexed; a unit test of the indexer passes whether or not the
// query reads it. Only a real search over a real index can fail for the right reason.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class SearchInTheCallersDayTests
{
    private readonly E2EApiFactory _factory;

    public SearchInTheCallersDayTests(E2EApiFactory factory) => _factory = factory;

    // Summer, so Zurich is +02:00 and the shift is unmistakably a DAY change rather than a near miss. 22:30
    // UTC on the 15th is 00:30 on the 16th in Zurich.
    private const string StoredDate = "2026-07-15";
    private const string StoredTime = "22:30";
    private const string LocalDate = "2026-07-16";
    private const string Zurich = "Europe/Zurich";

    [Fact]
    public async Task A_document_stamped_late_on_the_fifteenth_UTC_is_found_on_the_sixteenth_in_Zurich()
    {
        var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        // One run-unique term so a rerun against the shared index cannot collide, and so every assertion below
        // can be scoped to THIS run's documents rather than to whatever else the corpus holds.
        var term = $"zzt{Guid.NewGuid():N}";
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"tzsearch-{term}" })).GetProperty("id").GetGuid();

        var timed = await CreateDocAsync(owner, repoId, "timed", term, StoredDate, StoredTime);

        // THE DELIBERATE EXCEPTION, in the same corpus so the two cannot be tested apart: a document with no
        // time never was an instant. Somebody chose the 16th; it is the 16th in every zone, and must be found
        // by both searches below. Testing only the timed half would let a "convert everything" implementation
        // pass while quietly moving every date-only document by the offset.
        var dateOnly = await CreateDocAsync(owner, repoId, "dateonly", term, LocalDate, documentTime: null);

        // Indexed — polled on the free-text term, which needs none of the machinery under test. Polling on the
        // date filter instead would make an indexing delay indistinguishable from the conversion not working.
        await PollAsync(async () => (await SearchIdsAsync(owner, term, zone: null)).IsSupersetOf([timed, dateOnly]),
            "both documents are indexed");

        // 1) THE BUG. In Zurich the timed document IS the 16th, so a search for the 16th must return it —
        //    alongside the date-only one, which is the 16th everywhere.
        var inZurich = await SearchIdsAsync(owner, term, Zurich, $"system[documentDate][eq]={LocalDate}");
        Assert.Contains(timed, inZurich);
        Assert.Contains(dateOnly, inZurich);

        // 2) THE CONTROL, and the half that makes assertion 1 mean something. Asked as UTC, the SAME document
        //    is not on the 16th at all — it is on the 15th. Without this, an implementation that simply
        //    returned everything for any date filter would pass.
        var inUtc = await SearchIdsAsync(owner, term, zone: null, $"system[documentDate][eq]={LocalDate}");
        Assert.DoesNotContain(timed, inUtc);
        Assert.Contains(dateOnly, inUtc);   // still the 16th: a date-only document does not move

        // 3) The other side of the same coin: in Zurich the timed document is NOT on the 15th any more, and
        //    the date-only one never claimed to be. A conversion applied in the wrong direction passes 1 and
        //    fails here.
        var fifteenthInZurich = await SearchIdsAsync(owner, term, Zurich, $"system[documentDate][eq]={StoredDate}");
        Assert.DoesNotContain(timed, fifteenthInZurich);
        Assert.DoesNotContain(dateOnly, fifteenthInZurich);
    }

    [Fact]
    public async Task The_stored_preference_beats_the_devices_header()
    {
        // A user, because the preference hangs off a user account — and because this is the case a service
        // account cannot exercise: the header says one thing, the account says another, and the explicit
        // choice must win. A device in another zone silently undoing a setting the user made is the failure
        // this asserts against.
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"tzpref-{Guid.NewGuid():N}@e2e.local";
        const string password = "tzpref-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Zoned", canManageRepositories: true, isTenantAdmin: true);
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var term = $"zzp{Guid.NewGuid():N}";
        var repoId = (await TestJson.Post(api, "/api/repositories", new { name = $"tzpref-{term}" })).GetProperty("id").GetGuid();
        var timed = await CreateDocAsync(api, repoId, "timed", term, StoredDate, StoredTime);

        await PollAsync(async () => (await SearchIdsAsync(api, term, zone: null)).Contains(timed), "the document is indexed");

        (await api.PutAsJsonAsync("/api/me/time-zone", new { timeZoneId = Zurich })).EnsureSuccessStatusCode();

        // The header says UTC, the preference says Zurich. Zurich wins, so the document is on the 16th.
        var hits = await SearchIdsAsync(api, term, zone: "UTC", $"system[documentDate][eq]={LocalDate}");
        Assert.Contains(timed, hits);
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    private static async Task<Guid> CreateDocAsync(
        HttpClient owner, Guid parentId, string name, string term, string documentDate, string? documentTime)
    {
        var docId = (await TestJson.Post(owner, $"/api/documents/{parentId}/children", new { name = $"{name}-{Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var created = await TestJson.Post(owner, $"/api/documents/{docId}/versions", new { fileExtension = ".txt" });
        var versionId = created.GetProperty("id").GetGuid();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes($"marker {term}")))).EnsureSuccessStatusCode();
        }

        await TestJson.Put(owner, $"/api/documents/{docId}/versions/{versionId}", new { });

        // The date is set AFTER the version is confirmed, and this PUT enqueues its own reindex — so the
        // indexed document carries the stamp rather than the filing default.
        (await owner.PutAsJsonAsync($"/api/documents/{docId}/versions/{versionId}/document-date",
            new { documentDate, documentTime })).EnsureSuccessStatusCode();

        return docId;
    }

    private static async Task<HashSet<Guid>> SearchIdsAsync(HttpClient client, string q, string? zone, string? filter = null)
    {
        var url = $"/api/search?q={Uri.EscapeDataString(q)}" + (filter is null ? string.Empty : $"&{filter}");

        // The zone rides as the header both clients now set on every request. Sent per call here rather than on
        // the client, because the point of two of the assertions is that the SAME corpus answers differently
        // depending on who is asking.
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (zone is not null)
        {
            request.Headers.Add("X-Time-Zone", zone);
        }

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var json = await TestJson.Read(response);
        return json.GetProperty("results").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToHashSet();
    }

    private static async Task PollAsync(Func<Task<bool>> condition, string what, int timeoutSeconds = 90)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        throw new Xunit.Sdk.XunitException($"Timed out after {timeoutSeconds}s waiting for: {what}");
    }
}
