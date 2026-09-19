using Microsoft.Extensions.Diagnostics.HealthChecks;
using SimplArchive.Api.HealthChecks;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.UnitTests;

// What each kind of database refusal becomes on the wire (#1287, ADR 0807).
//
// WHY THIS IS ITS OWN TEST. The probe's ability to SEE a refusal needs a real Postgres and is pinned in the
// E2E suite. What a seen refusal is worth — 200 or 503 — is a pure decision, and it is the half with
// consequences for a live cluster: get it wrong in the Degraded direction and a connection shortage pulls
// every replica out of its Service at the same instant, converting a shortage into an outage. A decision that
// costly should not need Docker to re-check.
public class ReadinessStatusMappingTests
{
    [Fact]
    public async Task A_database_that_accepts_a_new_connection_is_healthy()
    {
        Assert.Equal(HealthStatus.Healthy, (await CheckAsync(DatabaseReachability.Reachable)).Status);
    }

    [Fact]
    public async Task A_refused_credential_is_unhealthy_so_the_instance_leaves_rotation()
    {
        var result = await CheckAsync(new DatabaseReachability(DatabaseReachabilityState.CredentialRefused, "28P01"));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("28P01", result.Description);
    }

    [Fact]
    public async Task A_connection_limit_is_degraded_so_the_instance_keeps_its_traffic()
    {
        // Degraded maps to 200 in ASP.NET Core's default status mapping, which /health/ready leaves
        // uncustomized (ADR 0205) — so this assertion IS the "keeps serving" claim, not a proxy for it.
        var result = await CheckAsync(new DatabaseReachability(DatabaseReachabilityState.Saturated, "53300"));

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("53300", result.Description);
    }

    [Fact]
    public async Task A_server_that_never_answered_is_unhealthy_and_says_so_without_a_sqlstate()
    {
        var result = await CheckAsync(new DatabaseReachability(DatabaseReachabilityState.Unreachable, null));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.DoesNotContain("(", result.Description);
    }

    [Fact]
    public async Task The_description_never_carries_the_drivers_own_message()
    {
        // /health/ready is anonymous by design (ADR 0205), and a driver's exception text names the host and the
        // login it tried. The descriptions are our own wording plus the server's SQLSTATE; this pins that a
        // later "include the real error for debuggability" edit has to argue with a test first.
        foreach (var state in new[]
        {
            DatabaseReachabilityState.CredentialRefused,
            DatabaseReachabilityState.Saturated,
            DatabaseReachabilityState.Unreachable,
        })
        {
            var description = (await CheckAsync(new DatabaseReachability(state, "28P01"))).Description;

            Assert.DoesNotContain("password authentication failed", description);
            Assert.DoesNotContain("Host=", description);
            Assert.DoesNotContain("Username=", description);
        }
    }

    private static Task<HealthCheckResult> CheckAsync(DatabaseReachability reachability) =>
        new DatabaseHealthCheck(new StubProbe(reachability))
            .CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

    private sealed class StubProbe(DatabaseReachability reachability) : IDatabaseReachabilityProbe
    {
        public ValueTask<DatabaseReachability> CheckAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(reachability);
    }
}
