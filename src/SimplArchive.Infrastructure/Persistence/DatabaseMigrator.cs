using Microsoft.EntityFrameworkCore;

namespace SimplArchive.Infrastructure.Persistence;

// Applies EF Core migrations over a dedicated connection string — used for the "migration owner" identity
// (ADR "Dedicated migration owner role"): DDL migrations run as a role that owns the schema, while the running
// app uses a least-privilege dynamic role for normal (DML) requests. Provider-specific (Npgsql) code stays in
// Infrastructure; the tenant accessor is irrelevant to MigrateAsync, so a default (no-tenant) one is used.
public static class DatabaseMigrator
{
    public static async Task MigrateAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        var options = new DbContextOptionsBuilder<SimplArchiveDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        await using var context = new SimplArchiveDbContext(options, new CurrentTenantAccessor());
        await context.Database.MigrateAsync(cancellationToken);
    }
}
