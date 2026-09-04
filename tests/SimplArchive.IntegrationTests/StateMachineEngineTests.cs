using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.ModuleAbi;
using SimplArchive.TestModule;

namespace SimplArchive.IntegrationTests;

// The state-machine engine (ADR 0742) against the real DbContext and the real TestModule definition:
// statuses DERIVE on every ask (nothing stored — filing a certificate changes the answer with no event),
// refusals are diagnoses (code + value + sentence), the temporarily-void flag overrides valid dates (the
// epic's medical shape), facts flow from the module's registered provider, and a transition runs its
// handler only through a green guard. Deterministic given documents + facts + clock — which is exactly why
// these tests need no host.
public class StateMachineEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    private sealed class TestUserAccessor : ICurrentUserAccessor
    {
        public Guid? UserId { get; set; }
    }

    private sealed class TestServiceAccountAccessor : ICurrentServiceAccountAccessor
    {
        public Guid? ServiceAccountId { get; set; }
    }

    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, Guid? tenantId = null)
    {
        var options = new DbContextOptionsBuilder<SimplArchiveDbContext>()
            .UseSqlite(connection)
            .Options;
        return new SimplArchiveDbContext(options, new CurrentTenantAccessor { TenantId = tenantId });
    }

    private sealed record Rig(SimplArchiveDbContext Context, StateMachineEngine Engine, ModuleArchiveFacade Facade, Guid DossierId, IServiceProvider Services);

    private static async Task<Rig> RigAsync(SqliteConnection connection)
    {
        using (var setup = CreateContext(connection))
        {
            await setup.Database.EnsureCreatedAsync();
        }

        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var rootId = Guid.NewGuid();
        using (var seed = CreateContext(connection))
        {
            seed.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
            seed.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "e@example.com", DisplayName = "E", CreatedAt = DateTimeOffset.UtcNow });
            seed.Documents.Add(new Document { Id = rootId, TenantId = tenantId, Name = "Root", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
            await seed.SaveChangesAsync();
        }

        var module = new TestModule.TestModule();
        using (var seedContext = CreateContext(connection, tenantId))
        {
            await new ModuleMaskSeeder(seedContext, NullLogger<ModuleMaskSeeder>.Instance).SeedAsync(module, tenantId);
        }

        // The definitions the way the host builds them: the module declares, the catalog holds.
        var catalog = new StateMachineCatalog();
        module.DefineStateMachines(catalog);

        var context = CreateContext(connection, tenantId);
        var facade = new ModuleArchiveFacade(context, new TestUserAccessor { UserId = userId }, new TestServiceAccountAccessor());

        // The module's OWN context, the way the host wires it (ADR 0738): the CORE connection, so the
        // engine's transaction covers projection writes; CreateTables rather than EnsureCreated, which is
        // all-or-nothing per database and silently does nothing beside the core's existing tables.
        var services = new ServiceCollection();
        module.ConfigureServices(services);
        services.AddSingleton<IModuleArchiveFacade>(facade);
        services.AddDbContext<TestReadModelContext>(options => options.UseSqlite(connection));
        var provider = services.BuildServiceProvider();
        try
        {
            Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions
                .GetService<Microsoft.EntityFrameworkCore.Storage.IRelationalDatabaseCreator>(
                    provider.GetRequiredService<TestReadModelContext>())
                .CreateTables();
        }
        catch (Exception)
        {
            // already created on this connection
        }

        var readModels = new ModuleReadModelCatalog([typeof(TestReadModelContext)]);
        var dossierId = await facade.CreateDocumentAsync(rootId, TestModule.TestModule.DossierMaskId, "Dossier");
        return new Rig(context, new StateMachineEngine(context, catalog, facade, provider, readModels), facade, dossierId, provider);
    }

    // The fact is the module's read model now (ADR 0738): steering it means writing the projection the
    // provider reads, exactly as the log-entry handler does.
    private static async Task SetLandingsAsync(Rig rig, int count)
    {
        var db = rig.Services.GetRequiredService<TestReadModelContext>();
        var row = await db.LandingCounters.FindAsync(rig.DossierId);
        if (row is null)
        {
            row = new TestLandingCounter { DossierId = rig.DossierId };
            db.LandingCounters.Add(row);
        }

        row.Count = count;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_valid_certificate_and_enough_landings_satisfy_the_status()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var rig = await RigAsync(connection);
        await SetLandingsAsync(rig, 5);
        await rig.Facade.CreateDocumentAsync(rig.DossierId, TestModule.TestModule.CertificateMaskId, "Medical",
            new Dictionary<string, string> { ["Valid to"] = "2027-01-01" });

        var result = await rig.Engine.EvaluateStatusAsync("test-pilot", "MayAct", rig.DossierId, Now);

        Assert.True(result.Satisfied);
        Assert.Empty(result.Failed);
    }

    [Fact]
    public async Task An_expired_certificate_fails_with_the_date_in_the_diagnosis()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var rig = await RigAsync(connection);
        await SetLandingsAsync(rig, 5);
        await rig.Facade.CreateDocumentAsync(rig.DossierId, TestModule.TestModule.CertificateMaskId, "Medical",
            new Dictionary<string, string> { ["Valid to"] = "2026-01-01" });

        var result = await rig.Engine.EvaluateStatusAsync("test-pilot", "MayAct", rig.DossierId, Now);

        Assert.False(result.Satisfied);
        var failure = Assert.Single(result.Failed);
        Assert.Equal("test.certificate-expired", failure.Code);
        Assert.Equal("2026-01-01", failure.Value);
        Assert.Contains("2026-01-01", failure.Text); // the sentence is a diagnosis, not a verdict (ADR 0742)
    }

    [Fact]
    public async Task A_missing_certificate_fails_because_absence_of_evidence_is_absence_of_the_right()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var rig = await RigAsync(connection);
        await SetLandingsAsync(rig, 5);

        var result = await rig.Engine.EvaluateStatusAsync("test-pilot", "MayAct", rig.DossierId, Now);

        Assert.False(result.Satisfied);
        Assert.Contains(result.Failed, f => f.Code == "test.certificate-expired");
    }

    [Fact]
    public async Task Temporarily_void_overrides_a_valid_date()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var rig = await RigAsync(connection);
        await SetLandingsAsync(rig, 5);
        // The epic's medical shape: dates read valid, the flag says otherwise — and the flag must win,
        // because it exists precisely for the case the dates cannot express (illness, medication).
        await rig.Facade.CreateDocumentAsync(rig.DossierId, TestModule.TestModule.CertificateMaskId, "Medical",
            new Dictionary<string, string> { ["Valid to"] = "2027-01-01", ["Temporarily void"] = "true" });

        var result = await rig.Engine.EvaluateStatusAsync("test-pilot", "MayAct", rig.DossierId, Now);

        Assert.False(result.Satisfied);
        Assert.Equal("test.certificate-void", Assert.Single(result.Failed).Code);
    }

    [Fact]
    public async Task A_fact_below_the_minimum_fails_with_the_value()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var rig = await RigAsync(connection);
        await SetLandingsAsync(rig, 1);
        await rig.Facade.CreateDocumentAsync(rig.DossierId, TestModule.TestModule.CertificateMaskId, "Medical",
            new Dictionary<string, string> { ["Valid to"] = "2027-01-01" });

        var result = await rig.Engine.EvaluateStatusAsync("test-pilot", "MayAct", rig.DossierId, Now);

        Assert.False(result.Satisfied);
        var failure = Assert.Single(result.Failed);
        Assert.Equal("test.recency", failure.Code);
        Assert.Equal("1 recent landings; 3 required.", failure.Text);
    }

    [Fact]
    public async Task Filing_a_certificate_changes_the_answer_with_no_event_anywhere()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var rig = await RigAsync(connection);
        await SetLandingsAsync(rig, 5);

        Assert.False((await rig.Engine.EvaluateStatusAsync("test-pilot", "MayAct", rig.DossierId, Now)).Satisfied);

        // The derived-status decision's whole point (ADRs 0740/0742): the office files the certificate —
        // by hand, through any path, even during a deactivation window — and the next ask says yes.
        await rig.Facade.CreateDocumentAsync(rig.DossierId, TestModule.TestModule.CertificateMaskId, "Medical",
            new Dictionary<string, string> { ["Valid to"] = "2027-01-01" });

        Assert.True((await rig.Engine.EvaluateStatusAsync("test-pilot", "MayAct", rig.DossierId, Now)).Satisfied);
    }

    [Fact]
    public async Task A_transition_runs_its_handler_only_through_a_green_guard()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var rig = await RigAsync(connection);
        await SetLandingsAsync(rig, 5);

        // Red guard (no certificate): refused with the diagnosis, and the handler did NOT run.
        var refused = await rig.Engine.ExecuteTransitionAsync("test-pilot", "log-entry", rig.DossierId, Now);
        Assert.False(refused.Satisfied);
        Assert.Empty(await rig.Facade.GetChildrenAsync(rig.DossierId, TestModule.TestModule.CertificateMaskId));

        // Green guard: the handler's document write happened through the facade, under the invariants.
        await rig.Facade.CreateDocumentAsync(rig.DossierId, TestModule.TestModule.CertificateMaskId, "Medical",
            new Dictionary<string, string> { ["Valid to"] = "2027-01-01" });
        var executed = await rig.Engine.ExecuteTransitionAsync("test-pilot", "log-entry", rig.DossierId, Now);
        Assert.True(executed.Satisfied);
        Assert.Single(await rig.Facade.GetChildrenAsync(rig.DossierId, TestModule.TestModule.EntryMaskId));
    }

    [Fact]
    public async Task A_handler_that_throws_rolls_the_whole_act_back()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var rig = await RigAsync(connection);

        // The fixture's "explode" transition WRITES a child through the facade — its own SaveChanges,
        // committed as far as the handler can tell — and then throws. The engine owns the transaction
        // (ADR 0737): what the handler wrote must never be seen, or a half-performed act would be a state
        // the next gate reads as real.
        var before = (await rig.Facade.GetChildrenAsync(rig.DossierId, TestModule.TestModule.EntryMaskId)).Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rig.Engine.ExecuteTransitionAsync("test-pilot", "explode", rig.DossierId, Now));
        Assert.Equal(before, (await rig.Facade.GetChildrenAsync(rig.DossierId, TestModule.TestModule.EntryMaskId)).Count);

        // ...and the PROJECTION write rolled back with it (ADR 0738): the exploding handler incremented
        // the module's counter before throwing, and one transaction means neither write survived.
        var db = rig.Services.GetRequiredService<TestReadModelContext>();
        Assert.Null(await db.LandingCounters.FindAsync(rig.DossierId));
    }
}
