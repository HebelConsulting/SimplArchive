using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SimplArchive.Infrastructure.Persistence;

/// <summary>
/// Lets `dotnet ef migrations add`/`dotnet ef database update` construct SimplArchiveDbContext without
/// a running host (no DI container, no real ICurrentTenantAccessor). The connection string here is only
/// used to generate/apply migrations at design time — never at runtime, where AddInfrastructure wires the
/// real one from configuration.
/// </summary>
public class SimplArchiveDbContextFactory : IDesignTimeDbContextFactory<SimplArchiveDbContext>
{
    public SimplArchiveDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<SimplArchiveDbContext>();
        optionsBuilder.UseNpgsql("Host=localhost;Database=simplarchive;Username=postgres;Password=postgres");

        return new SimplArchiveDbContext(optionsBuilder.Options, new DesignTimeCurrentTenantAccessor());
    }

    private sealed class DesignTimeCurrentTenantAccessor : Application.Abstractions.ICurrentTenantAccessor
    {
        public Guid? TenantId => null;
    }
}
