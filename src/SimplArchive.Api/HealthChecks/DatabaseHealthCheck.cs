using Microsoft.Extensions.Diagnostics.HealthChecks;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Api.HealthChecks;

/// <summary>
/// The one dependency every request actually needs (auth, tenant resolution, every controller) — see ADR 0205.
/// Tagged "ready" so it's only wired into /health/ready, not /health/live.
/// </summary>
/// <remarks>
/// <para>
/// <b>It asks for a NEW connection, not a pooled one</b> (#1287, ADR 0807). This check used to call
/// <c>Database.CanConnectAsync()</c>, which takes a connection from the pool — and a pooled open performs no
/// handshake. Running every ten seconds it kept one connection permanently warm and re-presented it forever,
/// so it held the single connection in the process that could not fail and reported on that one. Measured: six
/// consecutive ticks answered healthy with a password the database had already begun refusing, while a second
/// concurrent caller got <c>28P01</c>. The probe now performs a real handshake with the credential the app
/// would actually present.
/// </para>
/// <para>
/// <b>Why saturation is Degraded and not Unhealthy.</b> <c>Degraded</c> maps to 200, so the instance keeps its
/// traffic. That is deliberate: at a connection limit, connections already held keep working, and failing every
/// replica's readiness at the same instant would pull the entire Service — turning a shortage into an outage.
/// A refused credential is the opposite case: this instance genuinely cannot serve, and taking it out is right.
/// </para>
/// </remarks>
public class DatabaseHealthCheck : IHealthCheck
{
    private readonly IDatabaseReachabilityProbe _probe;

    public DatabaseHealthCheck(IDatabaseReachabilityProbe probe)
    {
        _probe = probe;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var reachability = await _probe.CheckAsync(cancellationToken);
        var sqlState = reachability.SqlState is { } state ? $" ({state})" : string.Empty;

        // The descriptions are OUR wording plus the server's SQLSTATE — never the driver's exception message,
        // which carries the host and login name. This endpoint is anonymous by design (ADR 0205).
        return reachability.State switch
        {
            DatabaseReachabilityState.Reachable => HealthCheckResult.Healthy(),
            DatabaseReachabilityState.Saturated => HealthCheckResult.Degraded(
                $"The database is at a connection limit{sqlState}. Connections already held keep working."),
            DatabaseReachabilityState.CredentialRefused => HealthCheckResult.Unhealthy(
                $"The database refused this instance's credential{sqlState}."),
            _ => HealthCheckResult.Unhealthy($"A new database connection could not be opened{sqlState}."),
        };
    }
}
