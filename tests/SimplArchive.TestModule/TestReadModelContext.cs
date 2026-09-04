using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SimplArchive.ModuleAbi;

namespace SimplArchive.TestModule;

/// <summary>
/// The fixture's read-model context (ADR 0738): one counter table, module-prefixed, derived data only —
/// the landing count a transition handler maintains and the fact provider reads. The smallest complete
/// proof that a module owns its projections without the core's schema ever learning it exists.
/// </summary>
public sealed class TestReadModelContext(DbContextOptions options) : ModuleDbContext(options)
{
    public DbSet<TestLandingCounter> LandingCounters => Set<TestLandingCounter>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Fluent only, provider-agnostic (ADR 0738) — the same parity rule the core's model lives by.
        modelBuilder.Entity<TestLandingCounter>(counter =>
        {
            counter.ToTable("tm_landing_counters");
            counter.HasKey(c => c.DossierId);
        });
    }
}

/// <summary>Recent landings per dossier — DERIVED from the entry documents, rebuildable by contract.</summary>
public sealed class TestLandingCounter
{
    /// <summary>The subject dossier. Document ids are globally unique, so tenancy rides the key.</summary>
    public Guid DossierId { get; set; }

    public int Count { get; set; }
}

/// <summary>
/// Design-time factory for generating this module's OWN migrations (`dotnet ef migrations add … --project
/// tests/SimplArchive.TestModule`): PostgreSQL-shaped with the module's history table, exactly as the host
/// wires it at runtime. Never used at runtime — the host constructs the context itself.
/// </summary>
public sealed class TestReadModelContextFactory : IDesignTimeDbContextFactory<TestReadModelContext>
{
    public TestReadModelContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder();
        builder.UseNpgsql("Host=localhost;Database=design-time-only",
            npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory_test_module"));
        return new TestReadModelContext(builder.Options);
    }
}
