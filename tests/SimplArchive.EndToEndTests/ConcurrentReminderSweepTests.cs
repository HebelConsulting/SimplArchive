using System.Linq;

namespace SimplArchive.EndToEndTests;

// Two sweeps at once fire a one-shot reminder ONCE (#1425, root cause of #1399).
//
// This is the arrangement, not a contrived race. ADR 0808 runs two app instances (`api` and `api-b` on compose
// and the kiosk, `replicaCount: 2` on the chart) and `DocumentReminderWorker` is registered unconditionally in
// both, each on its own 60-second timer. So two sweeps overlap in every multi-instance deployment.
//
// The old sweep read every pending reminder, built its notifications and saved once at the end — the correct
// TRANSACTIONAL shape (ADR 0794), with no exclusion. Both sweeps saw `FiredAt == null` before either committed,
// so both acted, and `DocumentReminder` is not `IConcurrencyTracked` so the second write did not even conflict:
// it succeeded silently. Measured before the fix: `acted=[1,1]`, TWO notifications, first try.
//
// #1399 found this as a leg-only test failure and guessed the fix belonged in the test — that a reminder firing
// twice because two sweeps ran was "arguably correct behaviour". It is not: a one-shot reminder fires once, and
// the assertion that noticed was the only thing watching.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class ConcurrentReminderSweepTests
{
    private readonly E2EApiFactory _factory;

    public ConcurrentReminderSweepTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Two_sweeps_at_once_fire_a_one_shot_reminder_exactly_once()
    {
        var reader = await DueReminderAsync(recurrence: 0);

        var acted = await Task.WhenAll(_factory.RunReminderSweepAsync(), _factory.RunReminderSweepAsync());

        // ONE notification. The count is the assertion — `Single` would also pass on zero-plus-an-exception, and
        // a `Contains` would pass on two, which is the defect.
        Assert.Equal(1, await FiredCountAsync(reader));

        // And exactly one sweep claimed it. Asserted because it is the MECHANISM: if both reported acting, the
        // notification count could still be 1 by luck of ordering, and the next change would break it again.
        Assert.Equal(1, acted.Count(a => a >= 1));
    }

    [Fact]
    public async Task Two_sweeps_at_once_advance_a_recurring_reminder_once()
    {
        // The recurring branch claims differently — a compare-and-swap on `RemindAt` rather than on `FiredAt`,
        // because a recurring reminder is never "done". Covered separately for exactly that reason: a fix that
        // only guarded the one-shot would leave a weekly reminder notifying twice every week.
        var reader = await DueReminderAsync(recurrence: 2);

        await Task.WhenAll(_factory.RunReminderSweepAsync(), _factory.RunReminderSweepAsync());

        Assert.Equal(1, await FiredCountAsync(reader));
    }

    [Fact]
    public async Task A_single_sweep_still_fires_the_reminder()
    {
        // The anti-vacuous half: an exclusion that excluded EVERYTHING would pass both tests above. This is the
        // one that says the claim lets the winner through.
        var reader = await DueReminderAsync(recurrence: 0);

        Assert.True(await _factory.RunReminderSweepAsync() >= 1);
        Assert.Equal(1, await FiredCountAsync(reader));
    }

    private sealed record Reader(HttpClient Api, Guid DocumentId);

    private async Task<int> FiredCountAsync(Reader reader) =>
        (await TestJson.Get(reader.Api, "/api/notifications")).GetProperty("notifications").EnumerateArray()
            .Count(n => n.GetProperty("type").GetString() == "DocumentReminder"
                        && n.GetProperty("documentId").GetGuid() == reader.DocumentId);

    /// <summary>A user with one back-dated, not-yet-fired reminder on a document of their own.</summary>
    private async Task<Reader> DueReminderAsync(int recurrence)
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        const string password = "sweep-1234";
        var email = $"sweep-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, password, "Sweep Reader");
        await _factory.GrantTenantAdminAsync(email);
        var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        // Its own document, because this test MUTATES what it asserts on (the reminder fires, the notification
        // lands) — shared seed data is for reading.
        var repoId = (await TestJson.Post(owner, "/api/repositories",
            new { name = $"Sweep {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var documentId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children",
            new { name = $"sweep-doc-{Guid.NewGuid():N}" })).GetProperty("id").GetGuid();

        var created = await TestJson.Post(api, $"/api/documents/{documentId}/reminders",
            new { remindAt = DateTimeOffset.UtcNow.AddDays(1), recurrence });
        await _factory.BackdateReminderAsync(created.GetProperty("id").GetGuid());

        return new Reader(api, documentId);
    }
}
