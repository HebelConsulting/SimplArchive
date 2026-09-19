using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Startup;

/// <summary>
/// Serialises the startup seeding across instances, so two processes booting together cannot both insert the
/// same well-known row (#1295).
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotent is not the same as concurrency-safe, and that difference is the whole bug.</b> Every startup
/// seeder is idempotent in the sense of *safe to run twice*: check whether the row exists, insert it if not.
/// Run twice **at once** against an empty database, both processes find it absent, both insert, and the loser
/// gets
/// <code>23505: duplicate key value violates unique constraint "IX_OpenIddictApplications_ClientId"</code>
/// which reaches the <c>AppDomain.UnhandledException</c> backstop and terminates it.
/// </para>
/// <para>
/// <b>The damage is not the crash.</b> The process exits 0, <c>restart: unless-stopped</c> brings it back, and
/// the retry succeeds because the row now exists — so the app self-heals within seconds. What does not heal is
/// everything that was waiting on it: Docker Compose saw the api report unhealthy and abandoned its dependants,
/// which on the kiosk left the MTA unstarted and ingress mail silently down. And because the kiosk wipes and
/// reseeds nightly, the empty-database window this race needs came round **every night**.
/// </para>
/// <para>
/// <b>Why a lock and not a <c>23505</c> catch.</b> The race is not specific to OpenIddict: the well-known
/// masks, the module masks, the bootstrap administrator and both tenant seeders are all check-then-insert. A
/// lock fixes the region once instead of one constraint at a time, and it works where there is no "first
/// instance" to privilege — which is the Kubernetes case, since the chart runs two replicas by default and has
/// exactly this race on a first deploy.
/// </para>
/// <para>
/// <b>Why the connection is opened explicitly.</b> A session-level advisory lock belongs to its connection. Let
/// EF close the connection between commands — which it does by default — and the lock is released with it,
/// leaving a lock that looks taken and is not. So the connection is opened here and held until disposal.
/// </para>
/// <para>
/// <b>What happens if a process dies holding it:</b> nothing that needs cleaning up. Postgres releases a
/// session advisory lock when the connection drops, so the next instance acquires it normally.
/// </para>
/// </remarks>
public sealed class StartupSeedLock : IAsyncDisposable
{
    /// <summary>
    /// An arbitrary but FIXED key — every instance must choose the same number or they do not exclude each
    /// other. Derived from nothing, deliberately: a key computed from a name would invite someone to change
    /// the name and silently split the lock in two.
    /// </summary>
    private const long Key = 0x51_4D_50_4C_53_45_45_44; // "SMPLSEED"

    private readonly SimplArchiveDbContext? _dbContext;
    private readonly ILogger _logger;

    private StartupSeedLock(SimplArchiveDbContext? dbContext, ILogger logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <summary>
    /// Takes the lock, waiting for any other instance that is already seeding. A no-op on a provider without
    /// advisory locks (SQLite in tests), where there is only ever one process anyway.
    /// </summary>
    public static async Task<StartupSeedLock> AcquireAsync(SimplArchiveDbContext dbContext, ILogger logger)
    {
        if (!dbContext.Database.IsNpgsql())
        {
            return new StartupSeedLock(null, logger);
        }

        await dbContext.Database.GetDbConnection().OpenAsync();
        await dbContext.Database.ExecuteSqlRawAsync("SELECT pg_advisory_lock({0})", Key);
        logger.LogDebug("Startup seeding lock acquired; any sibling instance waits here until seeding completes.");
        return new StartupSeedLock(dbContext, logger);
    }

    public async ValueTask DisposeAsync()
    {
        if (_dbContext is null)
        {
            return;
        }

        await _dbContext.Database.ExecuteSqlRawAsync("SELECT pg_advisory_unlock({0})", Key);
        await _dbContext.Database.GetDbConnection().CloseAsync();
        _logger.LogDebug("Startup seeding lock released.");
    }
}
