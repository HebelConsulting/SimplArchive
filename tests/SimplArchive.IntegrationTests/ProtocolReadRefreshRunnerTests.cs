using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Api.Modules;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Modules;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.ModuleAbi;
using SimplArchive.TestModule;

namespace SimplArchive.IntegrationTests;

// The protocol-read populate hook (ABI 0.27, ADR 0810, issue #1286) — the gate, against the real DbContext,
// the real engine and the real TestModule declarations.
//
// The defect being closed: the hook was invoked by the two SimplArchive clients and by nothing else, while
// the ephemeral sweep purged expired content regardless of who was looking. A user reaching the archive over
// a mounted drive or a mail client therefore saw an EMPTY weather folder — not stale, not an error, empty —
// and nothing on that path could say why.
//
// The gate has to be exactly two ANDed answers, and every test below is one leg of that: the MODULE says its
// source may be fetched on a protocol read (a public weather report and a licensed feed are not the same
// answer, which is why the declaration is the module's), and the TENANT ADMINISTRATOR says a PROPFIND here is
// a person asking rather than an indexer. ADR 0756 rejected unattended fetching on legal grounds; a gate that
// silently defaulted to "yes" on either half would be that decision reversed by accident.
public class ProtocolReadRefreshRunnerTests
{
    private sealed class TestUserAccessor : ICurrentUserAccessor
    {
        public Guid? UserId { get; set; }
    }

    private sealed class TestServiceAccountAccessor : ICurrentServiceAccountAccessor
    {
        public Guid? ServiceAccountId { get; set; }
    }

    internal sealed record Rig(
        SimplArchiveDbContext Context,
        ProtocolReadRefreshRunner Runner,
        ModuleArchiveFacade Facade,
        Guid TenantId,
        Guid DossierId,
        StateMachineCatalog Catalog,
        StateMachineEngine Engine);

    internal static async Task<Rig> DiagRigAsync(SqliteConnection connection)
    {
        var rig = await RigAsync(connection);
        await EnableAsync(rig);
        return rig;
    }

    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, Guid? tenantId = null) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options,
            new CurrentTenantAccessor { TenantId = tenantId });

    private static async Task<Rig> RigAsync(SqliteConnection connection, bool moduleActive = true)
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
            if (moduleActive)
            {
                seed.ModuleActivations.Add(new ModuleActivation
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    ModuleId = "test-module",
                    ActivatedAt = DateTimeOffset.UtcNow,
                    SupportContractEndDate = DateTimeOffset.UtcNow.AddYears(1),
                });
            }

            await seed.SaveChangesAsync();
        }

        var module = new SimplArchive.TestModule.TestModule();
        using (var maskSeed = CreateContext(connection, tenantId))
        {
            await new ModuleMaskSeeder(maskSeed, NullLogger<ModuleMaskSeeder>.Instance).SeedAsync(module, tenantId);
        }

        // Through the module SCOPE, as Program.cs does — not straight onto the catalog, which is the shape
        // the other engine tests use. A machine declared straight on the catalog carries no ModuleId, and the
        // runner needs one to ask the two questions the gate is made of: is that module active here, and has
        // this tenant enabled it. Building the catalog the other way makes every gate answer "no" and every
        // negative test pass for the wrong reason — which is exactly what it did on the first run.
        var catalog = new StateMachineCatalog();
        module.DefineStateMachines(catalog.ForModule(module.ModuleId));

        var context = CreateContext(connection, tenantId);
        // The staging hook WRITES content, so the facade needs a store — the host wires one and a test facade
        // that omits it fails inside the handler, which this runner reports as the module's source failing.
        var facade = new ModuleArchiveFacade(
            context, new TestUserAccessor { UserId = userId }, new TestServiceAccountAccessor(),
            objectStorage: new InMemoryObjectStorage());

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

        var engine = new StateMachineEngine(context, catalog, facade, provider, new ModuleReadModelCatalog([typeof(TestReadModelContext)]));
        var dossierId = await facade.CreateDocumentAsync(rootId, SimplArchive.TestModule.TestModule.DossierMaskId, "Dossier");

        return new Rig(
            context,
            new ProtocolReadRefreshRunner(context, catalog, engine, NullLogger<ProtocolReadRefreshRunner>.Instance),
            facade,
            tenantId,
            dossierId,
            catalog,
            engine);
    }

    private static async Task EnableAsync(Rig rig, string value = "true")
    {
        rig.Context.ModuleSettingValues.Add(new ModuleSettingValue
        {
            Id = Guid.NewGuid(),
            TenantId = rig.TenantId,
            ModuleId = "test-module",
            Key = ProtocolReadRefreshSetting.Key,
            Value = value,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await rig.Context.SaveChangesAsync();
    }

    /// <summary>What the eligible hook staged, by the name only it writes.</summary>
    private static Task<int> StagedCountAsync(Rig rig) =>
        rig.Context.Documents.CountAsync(d => d.ParentId == rig.DossierId && d.Name == SimplArchive.TestModule.TestModule.ProtocolStagedName);

    [Fact]
    public async Task An_enabled_tenant_gets_the_content_the_protocol_read_could_not_fetch_before()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var rig = await RigAsync(connection);
        await EnableAsync(rig);

        await rig.Runner.RefreshAsync(rig.DossierId, "WebDAV PROPFIND", CancellationToken.None);

        Assert.Equal(1, await StagedCountAsync(rig));
    }

    [Fact]
    public async Task Without_the_tenant_setting_nothing_is_fetched()
    {
        // The half that is the whole point of ADR 0756's caution: eligibility is the module's answer, but a
        // PROPFIND is not evidence that a PERSON is asking — WebDAV clients enumerate on their own schedule.
        // So the default is off, and off must mean nothing happens at all.
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var rig = await RigAsync(connection);

        await rig.Runner.RefreshAsync(rig.DossierId, "WebDAV PROPFIND", CancellationToken.None);

        Assert.Equal(0, await StagedCountAsync(rig));
    }

    [Fact]
    public async Task An_inactive_module_is_not_run_even_when_the_setting_says_yes()
    {
        // A setting outlives an activation — the row stays when a licence lapses. Reading the setting without
        // re-asking the activation question would let a deactivated module keep reaching its source.
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var rig = await RigAsync(connection, moduleActive: false);
        await EnableAsync(rig);

        await rig.Runner.RefreshAsync(rig.DossierId, "WebDAV PROPFIND", CancellationToken.None);

        Assert.Equal(0, await StagedCountAsync(rig));
    }

    [Fact]
    public async Task A_folder_that_already_holds_unexpired_content_is_left_alone()
    {
        // The cooldown, and the reason it is STATELESS: unexpired staged content already present IS the record
        // that a fetch happened recently, so the content's own ExpiresAt is the clock and no cooldown table can
        // go stale. Without this, a file-manager window left open becomes the unattended scraper ADR 0756 ruled
        // out — one PROPFIND per poll, one fetch per PROPFIND.
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var rig = await RigAsync(connection);
        await EnableAsync(rig);

        await rig.Runner.RefreshAsync(rig.DossierId, "WebDAV PROPFIND", CancellationToken.None);

        // The marker has to be something a re-run would OVERWRITE. Asserting the document's id is unchanged
        // does NOT work and looked like it did: this module's hook replaces its staged document in place, so
        // the id is identical whether the second read fetched or not — the test passed with the cooldown
        // deleted. Stamping a far-future expiry gives the second read something to destroy: if it runs, the
        // handler rewrites ExpiresAt to about an hour out.
        var staged = await rig.Context.Documents
            .SingleAsync(d => d.ParentId == rig.DossierId && d.Name == SimplArchive.TestModule.TestModule.ProtocolStagedName);
        staged.ExpiresAt = DateTimeOffset.UtcNow.AddDays(10);
        await rig.Context.SaveChangesAsync();

        await rig.Runner.RefreshAsync(rig.DossierId, "WebDAV PROPFIND", CancellationToken.None);

        var after = await rig.Context.Documents
            .SingleAsync(d => d.ParentId == rig.DossierId && d.Name == SimplArchive.TestModule.TestModule.ProtocolStagedName);
        Assert.True(after.ExpiresAt > DateTimeOffset.UtcNow.AddDays(5),
            "The second read re-ran the hook: the far-future expiry was overwritten, so the cooldown did not hold.");
    }

    [Fact]
    public async Task Expired_content_is_refetched()
    {
        // The other side of the same clock — and the actual reported symptom. The sweep purges what has
        // expired, so a protocol reader arrives at an empty folder; "already fresh" must not be satisfied by
        // content whose window has closed, or the gate would answer "nothing to do" forever.
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var rig = await RigAsync(connection);
        await EnableAsync(rig);

        await rig.Runner.RefreshAsync(rig.DossierId, "WebDAV PROPFIND", CancellationToken.None);
        var staged = await rig.Context.Documents
            .SingleAsync(d => d.ParentId == rig.DossierId && d.Name == SimplArchive.TestModule.TestModule.ProtocolStagedName);
        var firstId = staged.Id;
        staged.ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1);
        await rig.Context.SaveChangesAsync();

        await rig.Runner.RefreshAsync(rig.DossierId, "WebDAV PROPFIND", CancellationToken.None);

        var now = await rig.Context.Documents
            .SingleAsync(d => d.ParentId == rig.DossierId && d.Name == SimplArchive.TestModule.TestModule.ProtocolStagedName);
        Assert.True(now.ExpiresAt > DateTimeOffset.UtcNow);
        Assert.NotEqual(Guid.Empty, firstId);
    }

    [Fact]
    public async Task A_hook_that_did_not_declare_eligibility_is_not_run()
    {
        // The module's half. `refresh` is the clients-only hook; enabling the tenant setting must not promote
        // it — otherwise the toggle would opt a module into a fetch its author never sanctioned, which is the
        // legal question ADR 0756 answered, decided by the wrong party.
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var rig = await RigAsync(connection);
        await EnableAsync(rig);

        await rig.Runner.RefreshAsync(rig.DossierId, "WebDAV PROPFIND", CancellationToken.None);

        Assert.Equal(0, await rig.Context.Documents
            .CountAsync(d => d.ParentId == rig.DossierId && d.Name == "Staged entry"));
    }

    [Fact]
    public async Task A_value_that_is_not_true_reads_as_off()
    {
        // The read side is a straight equality test, which is only safe because the PUT refuses anything but
        // "true"/"false". This pins the reading half of that contract: a row that somehow says "yes" is OFF,
        // never a third state, and never accidentally truthy.
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var rig = await RigAsync(connection);
        await EnableAsync(rig, "yes");

        await rig.Runner.RefreshAsync(rig.DossierId, "WebDAV PROPFIND", CancellationToken.None);

        Assert.Equal(0, await StagedCountAsync(rig));
    }

    [Fact]
    public async Task A_folder_no_machine_claims_is_not_touched()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var rig = await RigAsync(connection);
        await EnableAsync(rig);
        var plain = await rig.Facade.CreateDocumentAsync(rig.DossierId, SimplArchive.TestModule.TestModule.EntryMaskId, "Plain");

        await rig.Runner.RefreshAsync(plain, "IMAP SELECT", CancellationToken.None);

        Assert.Equal(0, await rig.Context.Documents.CountAsync(d => d.ParentId == plain));
    }
}
