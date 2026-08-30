namespace SimplArchive.Application.Abstractions;

/// <summary>
/// Supplies the CURRENT password for the database's fixed runtime login, re-asked whenever Npgsql opens a new
/// physical connection after the refresh interval has elapsed.
/// </summary>
/// <remarks>
/// <para>
/// It exists because the app used to read ONE credential at startup and keep it for the life of the process.
/// The dev stack made the consequence concrete: after ~2 days the credential's 24h lease had expired, Postgres
/// had revoked the role, and every new connection failed <c>28P01 password authentication failed</c> — a
/// permanently unhealthy api that only a restart could fix. <c>/health/ready</c> reported it correctly the whole
/// time, which is why an orchestrator would have masked it by restarting the pod, and why it went unnoticed
/// outside a long-lived dev container.
/// </para>
/// <para>
/// The interface deliberately hands back a PASSWORD and not a connection string. A dynamic credential mints a
/// new USERNAME per lease, and no amount of refreshing can swap a username underneath a live connection pool —
/// which is exactly why the runtime moved to a fixed login whose password OpenBao rotates as a database static
/// role. Anything wider than a password here would invite that lesson to be un-learned.
/// </para>
/// <para>
/// It lives in Application, not Api, because Infrastructure consumes it and may not reference Api (the layering
/// is enforced by ArchitectureTests). The OpenBao implementation is registered by the Api, which owns the
/// secrets client; with no implementation registered the connection string keeps its own password, which is
/// what every test and every non-OpenBao deployment does.
/// </para>
/// </remarks>
public interface IDatabasePasswordProvider
{
    /// <summary>
    /// The current password. Implementations must throw on failure rather than returning a stale or empty
    /// value: Npgsql distinguishes a successful refresh from a failed one, and keeps using the password it
    /// already has until the next attempt succeeds. Returning something wrong instead of throwing would replace
    /// a working credential with a broken one.
    /// </summary>
    ValueTask<string> GetPasswordAsync(CancellationToken cancellationToken);
}
