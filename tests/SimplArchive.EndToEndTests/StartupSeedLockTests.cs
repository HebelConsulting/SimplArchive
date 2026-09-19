using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Api.Startup;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.EndToEndTests;

// Two instances cannot seed at the same time (#1295).
//
// WHAT WENT WRONG, so the test's shape makes sense. Every startup seeder is idempotent in the sense of "safe
// to run twice" — check, then insert if absent. A second api instance made it "twice AT ONCE": both processes
// found the OpenIddict `blazor-client` row missing, both inserted, and the loser died on
// `23505 duplicate key value violates unique constraint`. The app self-healed on restart; what did not heal was
// Docker Compose seeing the api go unhealthy and abandoning the MTA waiting on it — every nightly reset.
//
// WHY THIS TESTS THE LOCK AND NOT THE SEEDING. Reproducing the original race would mean booting two real hosts
// against one empty database and hoping the interleaving lands — a test that passes for the wrong reason
// whenever the timing is kind. The lock is the thing that has to hold, it is testable deterministically, and a
// real Postgres is required either way because `pg_advisory_lock` is the mechanism.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class StartupSeedLockTests
{
    private readonly E2EApiFactory _factory;

    public StartupSeedLockTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task A_second_instance_waits_while_the_first_is_seeding()
    {
        await using var first = NewContext();
        await using var second = NewContext();

        var held = await StartupSeedLock.AcquireAsync(first, NullLogger.Instance);

        // The second instance must NOT get in. Asserted by the attempt still being unfinished after a wait
        // long enough that a non-blocking acquire would certainly have completed — this is the whole property.
        var blocked = StartupSeedLock.AcquireAsync(second, NullLogger.Instance);
        var finishedEarly = await Task.WhenAny(blocked, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.NotSame(blocked, finishedEarly);

        // …and it must get in once the first is done, rather than waiting for a timeout or deadlocking. The
        // release path matters as much as the block: a lock that is never released turns every subsequent boot
        // into a hang, which is a worse failure than the crash it replaced.
        await held.DisposeAsync();

        var acquired = await Task.WhenAny(blocked, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.Same(blocked, acquired);
        await (await blocked).DisposeAsync();
    }

    [Fact]
    public async Task The_lock_is_released_even_though_EF_would_close_the_connection_between_commands()
    {
        // The subtle half. A session-level advisory lock belongs to its CONNECTION, and EF closes the
        // connection between commands by default — which would release the lock while the guard still believed
        // it held it, i.e. a lock that looks taken and excludes nobody. AcquireAsync therefore opens the
        // connection explicitly and holds it. This asserts the visible consequence: after a full acquire and
        // release cycle, the next acquire succeeds immediately rather than blocking on a leaked lock.
        await using var context = NewContext();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var handle = await StartupSeedLock.AcquireAsync(context, NullLogger.Instance);
            await handle.DisposeAsync();
        }

        await using var other = NewContext();
        var again = StartupSeedLock.AcquireAsync(other, NullLogger.Instance);
        var completed = await Task.WhenAny(again, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(again, completed);
        await (await again).DisposeAsync();
    }

    // A context per "instance": the lock is per CONNECTION, so two callers sharing one context would be one
    // session and would not exclude each other — which would make this test pass while proving nothing.
    private SimplArchiveDbContext NewContext() =>
        _factory.Services.CreateScope().ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
}
