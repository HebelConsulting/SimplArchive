namespace SimplArchive.Application.Abstractions;

/// <summary>
/// Answers whether the database would accept a NEW connection right now, and — when it would not — which kind
/// of refusal it is. Used by the readiness health check (#1287, ADR 0807).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all, rather than the health check asking EF.</b> <c>Database.CanConnectAsync()</c>
/// takes a connection from Npgsql's pool, and a pooled open performs no handshake — it hands back a physical
/// connection that authenticated at some earlier time. A probe running every 10 seconds therefore keeps one
/// connection permanently warm, well inside Npgsql's 300s idle lifetime, and re-presents it forever: it holds
/// the single connection in the process that cannot fail, and reports on that one. Measured against a real
/// Postgres, six consecutive probe ticks answered "ok" with a password the database had already started
/// refusing, while a second concurrent caller got <c>28P01</c>.
/// </para>
/// <para>
/// <b>Why the answer is not a boolean.</b> A probe that fails on every refusal turns connection exhaustion into
/// an outage: every replica goes unready at the same instant, the orchestrator pulls the whole Service, and
/// traffic that pooled connections were still serving stops being served. So the caller needs to tell "the
/// credential is refused" (this instance is broken and should be taken out) from "the server is full" (this
/// instance is degraded, and pulling it makes things worse).
/// </para>
/// <para>
/// It lives in Application because the Api's health check consumes it and the Npgsql implementation lives in
/// Infrastructure, which Api may not reference for this purpose — the same placement, and the same reason, as
/// <see cref="IDatabasePasswordProvider"/>.
/// </para>
/// </remarks>
public interface IDatabaseReachabilityProbe
{
    /// <summary>
    /// Opens a fresh physical connection and closes it, reporting what happened. Never throws for a database
    /// that refused it — a refusal is the answer, not an error.
    /// </summary>
    ValueTask<DatabaseReachability> CheckAsync(CancellationToken cancellationToken);
}

/// <summary>What a fresh connection attempt found.</summary>
/// <param name="State">The classification the caller maps to a health status.</param>
/// <param name="SqlState">
/// The SQLSTATE the server returned, or <c>null</c> when it never answered (no listener, unroutable host,
/// timeout). Carried so a human reading a health response sees the server's own verdict rather than ours.
/// </param>
public readonly record struct DatabaseReachability(DatabaseReachabilityState State, string? SqlState)
{
    public static DatabaseReachability Reachable { get; } = new(DatabaseReachabilityState.Reachable, null);
}

public enum DatabaseReachabilityState
{
    /// <summary>A new connection was opened and closed.</summary>
    Reachable,

    /// <summary>
    /// The server refused the credential (<c>28P01</c>). Note that Postgres returns this for an unknown role
    /// as well as a wrong password — deliberately, so that a caller cannot enumerate usernames — so this means
    /// "this login does not work", not specifically "the password is out of date".
    /// </summary>
    CredentialRefused,

    /// <summary>
    /// The server is at a connection limit (<c>53300</c>), per-role or server-wide. Existing pooled connections
    /// are unaffected, which is why this is deliberately not the same answer as a refused credential.
    /// </summary>
    Saturated,

    /// <summary>Anything else: no listener, an unroutable host, a timeout, or a SQLSTATE we do not classify.</summary>
    Unreachable,
}
