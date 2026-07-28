using Microsoft.Extensions.Diagnostics.HealthChecks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.HealthChecks;

/// <summary>
/// The one dependency every request actually needs (auth, tenant resolution, every controller) — see ADR
/// "Health check endpoints". Tagged "ready" so it's only wired into /health/ready, not /health/live.
/// </summary>
public class DatabaseHealthCheck : IHealthCheck
{
    private readonly SimplArchiveDbContext _dbContext;

    public DatabaseHealthCheck(SimplArchiveDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var canConnect = await _dbContext.Database.CanConnectAsync(cancellationToken);

        return canConnect ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy();
    }
}
