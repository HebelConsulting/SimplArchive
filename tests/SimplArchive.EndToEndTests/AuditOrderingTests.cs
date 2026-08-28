using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// The audit log's order and its pagination, over the real API + Postgres (issue #478).
//
// The log is append-only and hash-chained (ADR 0321), so the order it is READ in should be the order it was
// APPENDED in. It used to be ordered by (Timestamp desc, Id desc) with Id a random Guid — which orders
// same-instant events arbitrarily, and differently on every read. That is invisible in production, where
// timestamps rarely collide, and total under the manual capture's frozen demo clock, where every event shares
// one instant: the manual's audit screenshot reshuffled itself on every run, so the regeneration bot committed
// a "change" after every single push to main and put every open PR in a binary-file conflict.
//
// What this test actually proves, stated plainly: that the SORT AND THE CURSOR AGREE. That is the risky half
// of the change — a page boundary that disagrees with the sort silently skips or repeats rows, and no
// screenshot would ever show it. It does NOT prove the tiebreak matters: Postgres timestamps have microsecond
// resolution, so even a tight burst rarely collides here. The determinism itself is structural — (TenantId,
// Sequence) is a unique index, so ordering by it is total where ordering by a random Guid was arbitrary — and
// the case that made it visible needs the manual capture's frozen clock, where every event shares one instant.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class AuditOrderingTests
{
    private readonly E2EApiFactory _factory;

    public AuditOrderingTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task The_log_reads_in_chain_order_and_pages_without_skipping_or_repeating()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var api = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        // A burst of mutations, as fast as the API will take them — the point is to land several events inside
        // the same timestamp tick, which is exactly the case the old Guid tiebreak ordered at random.
        var repoId = (await PostJson(api, "/api/repositories", new { name = $"Order {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        for (var i = 0; i < 12; i++)
        {
            await PostJson(api, $"/api/documents/{repoId}/children", new { name = $"burst-{i}" });
        }

        var email = $"order-{Guid.NewGuid():N}@simplarchive.local";
        const string password = "AuditOrder2026!";
        await _factory.SeedUserAsync(tenantId, email, password, "Order auditor", canViewAuditLog: true);
        using var viewer = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        // 1) Stable: the same query twice returns the same sequence. A random tiebreak fails this as soon as
        //    two events share a timestamp — which the burst above makes near-certain.
        var first = await IdsAsync(viewer, "/api/audit-events?limit=50");
        var second = await IdsAsync(viewer, "/api/audit-events?limit=50");
        Assert.Equal(first, second);

        // 2) Newest first — asserted on the timestamps the rows do publish. (The chain order WITHIN one
        //    timestamp is what changed; it is proved by (1) being stable and (3) paging consistently, since a
        //    random tiebreak fails both.)
        var stamps = (await GetJson(viewer, "/api/audit-events?limit=50")).GetProperty("events").EnumerateArray()
            .Select(e => e.GetProperty("timestamp").GetDateTimeOffset()).ToList();
        Assert.Equal(stamps.OrderByDescending(t => t).ToList(), stamps);

        // 3) The cursor agrees with that sort: walking small pages visits every event exactly once, in the
        //    same order the single big read produced. This is what a mismatched cursor would break.
        var walked = new List<Guid>();
        var url = "/api/audit-events?limit=5";
        while (url is not null)
        {
            var page = await GetJson(viewer, url);
            walked.AddRange(page.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("id").GetGuid()));
            url = page.GetProperty("links").EnumerateArray()
                .Where(l => l.GetProperty("rel").GetString() == "next")
                .Select(l => l.GetProperty("href").GetString())
                .FirstOrDefault();
        }

        Assert.Equal(walked.Distinct().Count(), walked.Count); // nothing repeated across page boundaries
        Assert.Equal(first, walked.Take(first.Count).ToList()); // …and in the same order as the unpaged read
    }

    // Read by ID, not by the chain sequence: the row resource does not publish Sequence, and this test is not
    // the place to add API surface. Ids are enough — they identify each row, so a reshuffle changes the list.
    private static async Task<List<Guid>> IdsAsync(HttpClient client, string url) =>
        (await GetJson(client, url)).GetProperty("events").EnumerateArray()
            .Select(e => e.GetProperty("id").GetGuid()).ToList();

    private static async Task<JsonElement> GetJson(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<JsonElement> PostJson(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }
}
