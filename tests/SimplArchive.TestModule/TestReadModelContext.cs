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

    /// <summary>What the projection hook SAW each time it was called (ABI 1.1, ADR 0848).</summary>
    public DbSet<TestProjectionProbe> ProjectionProbes => Set<TestProjectionProbe>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Fluent only, provider-agnostic (ADR 0738) — the same parity rule the core's model lives by.
        modelBuilder.Entity<TestLandingCounter>(counter =>
        {
            counter.ToTable("tm_landing_counters");
            counter.HasKey(c => c.DossierId);
        });

        modelBuilder.Entity<TestProjectionProbe>(probe =>
        {
            probe.ToTable("tm_projection_probes");
            probe.HasKey(p => p.DocumentId);
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
/// One call of the projection hook, and — the point of it — WHETHER THE HOOK COULD SEE ITS OWN SUBJECT.
/// </summary>
/// <remarks>
/// <para>
/// A row per projected document, written by <c>TestModule.DocumentProjected</c>. It records the document's
/// name as the hook read it back through the facade, or that the facade answered "no such document" — which
/// is the defect this fixture exists to catch and is otherwise invisible: the hook simply returns, having
/// written nothing and logged nothing, and only a rebuild ever fills the read model.
/// </para>
/// <para>
/// So a test must assert <see cref="SawName"/>, not merely that a row exists. "The hook ran" and "the hook
/// could do its job" are different facts, and the bug lives between them.
/// </para>
/// </remarks>
public sealed class TestProjectionProbe
{
    /// <summary>The projected document. Ids are globally unique, so tenancy rides the key.</summary>
    public Guid DocumentId { get; set; }

    /// <summary>The name the facade gave back, or empty when the facade could not see the document.</summary>
    public string SawName { get; set; } = string.Empty;

    /// <summary>What the core said about the document's fate, carried so a removal is distinguishable.</summary>
    public bool Removed { get; set; }

    /// <summary>How many times the hook has been called for this document.</summary>
    public int Calls { get; set; }
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
