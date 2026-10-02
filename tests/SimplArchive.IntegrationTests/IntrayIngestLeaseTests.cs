using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Infrastructure.Intray;

namespace SimplArchive.IntegrationTests;

// The storage-side lease that stops two app instances ingesting one dropped file (#1427).
//
// Every other sweep claims its work with a database compare-and-swap (ADR 0836); this one's state lives in
// object storage, which has no such primitive — so the lease is built on conditional writes, and these pin the
// three properties that make it a lease rather than a marker: exclusion, expiry, and a holder that finds out
// it was dispossessed.
public class IntrayIngestLeaseTests
{
    // A hand-rolled clock rather than Microsoft.Extensions.TimeProvider.Testing: the whole need is "return
    // this instant, and let me move it", and a package reference carries a licence-gate entry and a version to
    // keep for that.
    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private const string ClaimKey = "tenants/t/users/u/inbox/scan.tif.ingest.claim";

    private static async Task<IntrayIngestLease?> AcquireAsync(
        InMemoryObjectStorage storage, TestClock clock)
    {
        var existing = (await storage.ListObjectsAsync(ClaimKey)).FirstOrDefault(o => o.Key == ClaimKey);
        return await IntrayIngestLease.TryAcquireAsync(
            storage, NullLogger.Instance, ClaimKey, existing, clock, CancellationToken.None);
    }

    [Fact]
    public async Task Only_one_of_two_instances_takes_the_lease()
    {
        // The defect itself: both list before either writes, so without a conditional create both proceed and
        // one dropped file becomes two documents.
        var storage = new InMemoryObjectStorage();
        var clock = new TestClock(DateTimeOffset.Parse("2026-10-02T09:00:00Z"));

        await using var first = await AcquireAsync(storage, clock);
        await using var second = await AcquireAsync(storage, clock);

        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public async Task A_live_lease_is_not_taken_over()
    {
        var storage = new InMemoryObjectStorage();
        var clock = new TestClock(DateTimeOffset.Parse("2026-10-02T09:00:00Z"));
        await using var held = await AcquireAsync(storage, clock);
        Assert.NotNull(held);

        // Well inside the window: a slow ingest must not have its work stolen, which would produce exactly the
        // duplicate the lease exists to prevent.
        clock.Advance(IntrayIngestLease.StaleAfter - TimeSpan.FromSeconds(30));
        await using var thief = await AcquireAsync(storage, clock);

        Assert.Null(thief);
    }

    [Fact]
    public async Task A_lease_whose_holder_stopped_renewing_is_taken_over()
    {
        // The crash case, which every rolling update produces by design: the holder is gone, nothing will ever
        // clear the claim, and without expiry the file is stranded forever — "ingested twice" traded for
        // "never ingested".
        var storage = new InMemoryObjectStorage();
        var clock = new TestClock(DateTimeOffset.Parse("2026-10-02T09:00:00Z"));
        var abandoned = await AcquireAsync(storage, clock);
        Assert.NotNull(abandoned);

        // Age the claim as the store would see it, rather than waiting: the holder is dead and stopped beating.
        storage.Modified[ClaimKey] = clock.GetUtcNow() - IntrayIngestLease.StaleAfter - TimeSpan.FromSeconds(1);

        await using var taken = await AcquireAsync(storage, clock);
        Assert.NotNull(taken);
    }

    [Fact]
    public async Task A_takeover_conditioned_on_a_stale_tag_loses_to_whoever_got_there_first()
    {
        // Two instances can judge the same claim stale in the same instant. The conditional overwrite is what
        // makes exactly one of them win — delete-then-create would let a slow reclaimer wipe a claim the other
        // had already made, after which both believe they hold it.
        var storage = new InMemoryObjectStorage();
        var clock = new TestClock(DateTimeOffset.Parse("2026-10-02T09:00:00Z"));
        Assert.NotNull(await AcquireAsync(storage, clock));

        // Age it FIRST, then take the snapshot — which is the real order: an instance lists, and what it holds
        // is a claim that already looks stale. Snapshotting before ageing was this test's own first bug, and it
        // read as the takeover being broken rather than as the reclaimer correctly refusing a live lease.
        storage.Modified[ClaimKey] = clock.GetUtcNow() - IntrayIngestLease.StaleAfter - TimeSpan.FromSeconds(1);
        var stale = (await storage.ListObjectsAsync(ClaimKey)).Single(o => o.Key == ClaimKey);

        // The first reclaimer wins and the tag moves…
        await using var winner = await IntrayIngestLease.TryAcquireAsync(
            storage, NullLogger.Instance, ClaimKey, stale, clock, CancellationToken.None);
        Assert.NotNull(winner);

        // …so the second, still holding the tag it saw, is refused.
        await using var loser = await IntrayIngestLease.TryAcquireAsync(
            storage, NullLogger.Instance, ClaimKey, stale, clock, CancellationToken.None);
        Assert.Null(loser);
    }

    [Fact]
    public async Task Releasing_the_lease_lets_the_next_attempt_start_at_once()
    {
        // A transient failure should cost the next sweep, not a whole window. A crash releases nothing, which
        // is what the expiry above is for.
        var storage = new InMemoryObjectStorage();
        var clock = new TestClock(DateTimeOffset.Parse("2026-10-02T09:00:00Z"));

        var lease = await AcquireAsync(storage, clock);
        Assert.NotNull(lease);
        await lease!.DisposeAsync();

        await using var next = await AcquireAsync(storage, clock);
        Assert.NotNull(next);
    }
}

// The capability probe (#1427): the sweep refuses to run on a store that does not really honour conditional
// writes, because the dangerous failure is silent.
public class ConditionalWriteProbeTests
{
    [Fact]
    public async Task A_store_that_honours_conditional_writes_is_accepted()
    {
        var storage = new InMemoryObjectStorage();
        var probe = new ConditionalWriteProbe(storage, NullLogger<ConditionalWriteProbe>.Instance);

        Assert.True(await probe.SupportedAsync("tenants/t/.probe/"));
    }

    [Fact]
    public async Task A_store_that_IGNORES_the_condition_is_refused()
    {
        // THE CASE THIS EXISTS FOR, and the reason it is a probe rather than an assumption. A store that
        // refuses conditional writes fails loudly and gets fixed; one that ignores them answers 200 to every
        // writer, provides no exclusion whatever, and every dropped file is filed once per app instance — with
        // nothing in the logs and no way for the operator to tell from the outside.
        var storage = new InMemoryObjectStorage { IgnoresConditionalWrites = true };
        var probe = new ConditionalWriteProbe(storage, NullLogger<ConditionalWriteProbe>.Instance);

        Assert.False(await probe.SupportedAsync("tenants/t/.probe/"));
    }

    [Fact]
    public async Task The_probe_cleans_up_after_itself()
    {
        var storage = new InMemoryObjectStorage();
        var probe = new ConditionalWriteProbe(storage, NullLogger<ConditionalWriteProbe>.Instance);

        await probe.SupportedAsync("tenants/t/.probe/");

        Assert.DoesNotContain(storage.Objects.Keys, k => k.StartsWith("tenants/t/.probe/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_answer_is_measured_once()
    {
        // It measures the STORE, which does not change under a running app — and the probe writes twice, so
        // doing it per sweep would be two needless writes every five minutes, forever.
        var storage = new InMemoryObjectStorage();
        var probe = new ConditionalWriteProbe(storage, NullLogger<ConditionalWriteProbe>.Instance);

        Assert.True(await probe.SupportedAsync("tenants/t/.probe/"));

        storage.IgnoresConditionalWrites = true; // would now fail if it asked again
        Assert.True(await probe.SupportedAsync("tenants/t/.probe/"));
    }
}
