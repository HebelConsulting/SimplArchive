using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace SimplArchive.Infrastructure.Persistence;

/// <summary>
/// Turns the database's own "that password is wrong" into a refresh (#1274).
/// </summary>
/// <remarks>
/// <para>
/// A rotating credential can go stale between refreshes — most obviously when the host is suspended and the
/// refresh timer does not run. The database is the only party that knows for certain, and it says so plainly:
/// <c>28P01 password authentication failed</c>. Listening for that turns a permanent outage into a single
/// failed connection.
/// </para>
/// <para>
/// <b>It is NOT transparent recovery, and that is worth stating.</b> There is no retrying execution strategy
/// configured here, so the request that met the stale password still fails; the next one succeeds against the
/// re-read credential. Making it invisible would mean treating an authentication failure as transient, which
/// would also retry a genuinely wrong password rather than surfacing a misconfiguration — a different decision,
/// deliberately not taken here.
/// </para>
/// <para>
/// <b>Only 28P01.</b> Any other connection failure — the host being down, a network partition, a refused
/// connection — says nothing about the credential, and invalidating on those would send us to the secrets
/// store every time the database is briefly unreachable.
/// </para>
/// </remarks>
public sealed class DatabaseCredentialInterceptor : DbConnectionInterceptor
{
    // PostgreSQL's SQLSTATE for "password authentication failed for user". Spelled out rather than compared to
    // a message, which is localized by the server's lc_messages and would stop matching without warning.
    private const string InvalidPassword = "28P01";

    private readonly RefreshableDatabasePassword _password;

    public DatabaseCredentialInterceptor(RefreshableDatabasePassword password) => _password = password;

    public override void ConnectionFailed(DbConnection connection, ConnectionErrorEventData eventData)
    {
        InvalidateIfPasswordRejected(eventData);
        base.ConnectionFailed(connection, eventData);
    }

    public override Task ConnectionFailedAsync(
        DbConnection connection, ConnectionErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        InvalidateIfPasswordRejected(eventData);
        return base.ConnectionFailedAsync(connection, eventData, cancellationToken);
    }

    private void InvalidateIfPasswordRejected(ConnectionErrorEventData eventData)
    {
        // Unwrapped rather than matched on the outermost exception: EF and Npgsql both wrap, and a check that
        // only looked at the top would silently stop firing the day another layer was added.
        for (Exception? e = eventData.Exception; e is not null; e = e.InnerException)
        {
            if (e is PostgresException { SqlState: InvalidPassword })
            {
                _password.Invalidate();
                return;
            }
        }
    }
}
