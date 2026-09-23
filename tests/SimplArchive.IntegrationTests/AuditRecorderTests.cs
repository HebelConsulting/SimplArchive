using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Audit;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Audit;
using SimplArchive.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SimplArchive.IntegrationTests;

// Verifies the audit recorder (ADR "Audit trail (first slice)"): it resolves the current actor + a name
// snapshot and appends a tenant-scoped AuditEvent; RecordForActorAsync records with an explicit actor (the
// login POST, the background sweeps, the startup seeds); and when no actor is resolvable it WARNS and
// drops — not silently (#1312): the quiet no-op is how the EmailAbandoned events were never written in
// production while an explanatory comment said they were. Still a drop rather than a throw, because
// auditing must never break the action it records — the warning names the discarded action and the fix.
public class AuditRecorderTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, CurrentTenantAccessor tenantAccessor) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options, tenantAccessor);

    private static AuditRecorder CreateRecorder(
        SimplArchiveDbContext dbContext,
        CurrentTenantAccessor tenantAccessor,
        CurrentUserAccessor userAccessor,
        CurrentServiceAccountAccessor serviceAccountAccessor,
        CurrentPlatformAdministratorAccessor platformAdministratorAccessor) =>
        new(dbContext, userAccessor, serviceAccountAccessor, platformAdministratorAccessor, tenantAccessor, new CurrentImpersonationAccessor(), new CurrentSystemActorAccessor(), TimeProvider.System, NullLogger<AuditRecorder>.Instance);

    [Fact]
    public async Task RecordAsync_appends_event_with_actor_name_snapshot_scoped_to_the_current_tenant()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        var tenantAccessor = new CurrentTenantAccessor();
        var userAccessor = new CurrentUserAccessor();
        var serviceAccountAccessor = new CurrentServiceAccountAccessor();
        var platformAdministratorAccessor = new CurrentPlatformAdministratorAccessor();

        using (var setup = CreateContext(connection, tenantAccessor))
        {
            await setup.Database.EnsureCreatedAsync();
        }

        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Acme", CreatedAt = DateTimeOffset.UtcNow };
        var otherTenant = new Tenant { Id = Guid.NewGuid(), Name = "Other", CreatedAt = DateTimeOffset.UtcNow };
        var actor = new User { Id = Guid.NewGuid(), TenantId = tenant.Id, Email = "admin@acme.test", DisplayName = "Alice Admin", CreatedAt = DateTimeOffset.UtcNow };
        var targetId = Guid.NewGuid();

        using (var seed = CreateContext(connection, tenantAccessor))
        {
            seed.Tenants.AddRange(tenant, otherTenant);
            seed.Users.Add(actor);
            await seed.SaveChangesAsync();
        }

        tenantAccessor.TenantId = tenant.Id;
        userAccessor.UserId = actor.Id;

        using (var recordContext = CreateContext(connection, tenantAccessor))
        {
            var recorder = CreateRecorder(recordContext, tenantAccessor, userAccessor, serviceAccountAccessor, platformAdministratorAccessor);
            await recorder.RecordAsync("Document.Deleted", "Document", targetId, "Invoice", "cascade: 3");
        }

        using var readContext = CreateContext(connection, tenantAccessor);
        var evt = await readContext.AuditEvents.SingleAsync();

        Assert.Equal(tenant.Id, evt.TenantId);
        Assert.Equal(AuditActorType.User, evt.ActorType);
        Assert.Equal(actor.Id, evt.ActorId);
        Assert.Equal("Alice Admin", evt.ActorName);
        Assert.Equal("Document.Deleted", evt.Action);
        Assert.Equal("Document", evt.TargetType);
        Assert.Equal(targetId, evt.TargetId);
        Assert.Equal("Invoice", evt.TargetName);
        Assert.Equal("cascade: 3", evt.Details);

        // Tenant-scoped: a reader positioned at the other tenant sees none of it.
        tenantAccessor.TenantId = otherTenant.Id;
        using var otherContext = CreateContext(connection, tenantAccessor);
        Assert.Empty(await otherContext.AuditEvents.ToListAsync());
    }

    [Fact]
    public async Task RecordAsync_is_a_no_op_when_no_actor_is_resolvable()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        var tenantAccessor = new CurrentTenantAccessor();
        var userAccessor = new CurrentUserAccessor();
        var serviceAccountAccessor = new CurrentServiceAccountAccessor();
        var platformAdministratorAccessor = new CurrentPlatformAdministratorAccessor();

        using (var setup = CreateContext(connection, tenantAccessor))
        {
            await setup.Database.EnsureCreatedAsync();
        }

        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Acme", CreatedAt = DateTimeOffset.UtcNow };
        using (var seed = CreateContext(connection, tenantAccessor))
        {
            seed.Tenants.Add(tenant);
            await seed.SaveChangesAsync();
        }

        tenantAccessor.TenantId = tenant.Id; // tenant set, but no actor
        var log = new ListLogger();
        using (var recordContext = CreateContext(connection, tenantAccessor))
        {
            var recorder = new AuditRecorder(recordContext, userAccessor, serviceAccountAccessor,
                platformAdministratorAccessor, tenantAccessor, new CurrentImpersonationAccessor(),
                new CurrentSystemActorAccessor(), TimeProvider.System, log);
            await recorder.RecordAsync("Auth.LoggedIn");
        }

        using var readContext = CreateContext(connection, tenantAccessor);
        Assert.Empty(await readContext.AuditEvents.IgnoreQueryFilters().ToListAsync());

        // …and the drop is LOUD (#1312): a Warning names the discarded action and points at
        // RecordForActorAsync, so the next principal-less caller learns on the first event, not in an audit.
        var warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("Auth.LoggedIn", warning.Message, StringComparison.Ordinal);
        Assert.Contains("RecordForActorAsync", warning.Message, StringComparison.Ordinal);
    }

    private sealed class ListLogger : Microsoft.Extensions.Logging.ILogger<AuditRecorder>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public async Task RecordForActorAsync_records_with_the_explicit_actor()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        var tenantAccessor = new CurrentTenantAccessor();
        var userAccessor = new CurrentUserAccessor();
        var serviceAccountAccessor = new CurrentServiceAccountAccessor();
        var platformAdministratorAccessor = new CurrentPlatformAdministratorAccessor();

        using (var setup = CreateContext(connection, tenantAccessor))
        {
            await setup.Database.EnsureCreatedAsync();
        }

        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Acme", CreatedAt = DateTimeOffset.UtcNow };
        using (var seed = CreateContext(connection, tenantAccessor))
        {
            seed.Tenants.Add(tenant);
            await seed.SaveChangesAsync();
        }

        var userId = Guid.NewGuid();

        // The accessors are deliberately left unset — this mirrors the anonymous login POST.
        using (var recordContext = CreateContext(connection, tenantAccessor))
        {
            var recorder = CreateRecorder(recordContext, tenantAccessor, userAccessor, serviceAccountAccessor, platformAdministratorAccessor);
            await recorder.RecordForActorAsync(AuditActorType.User, userId, "Bob User", tenant.Id, "Auth.LoggedIn");
        }

        tenantAccessor.TenantId = tenant.Id;
        using var readContext = CreateContext(connection, tenantAccessor);
        var evt = await readContext.AuditEvents.SingleAsync();

        Assert.Equal(tenant.Id, evt.TenantId);
        Assert.Equal(AuditActorType.User, evt.ActorType);
        Assert.Equal(userId, evt.ActorId);
        Assert.Equal("Bob User", evt.ActorName);
        Assert.Equal("Auth.LoggedIn", evt.Action);
        Assert.Null(evt.TargetType);
    }

    /// <summary>
    /// #1329: a scope acting for no principal can name itself a SYSTEM actor, and RecordAsync then
    /// attributes to System/Guid.Empty with that name instead of warn-dropping — the department-mailbox
    /// delivery's contract, proven against the real recorder.
    /// </summary>
    [Fact]
    public async Task RecordAsync_falls_back_to_the_named_system_actor_when_no_principal_resolves()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        var tenantAccessor = new CurrentTenantAccessor();
        using var dbContext = CreateContext(connection, tenantAccessor);
        await dbContext.Database.EnsureCreatedAsync();

        var tenantId = Guid.NewGuid();
        dbContext.Tenants.Add(new Tenant { Id = tenantId, Name = "T", CreatedAt = DateTimeOffset.UtcNow });
        await dbContext.SaveChangesAsync();
        tenantAccessor.TenantId = tenantId;

        var systemActor = new CurrentSystemActorAccessor { Name = "Inbound mail" };
        var recorder = new AuditRecorder(dbContext, new CurrentUserAccessor(), new CurrentServiceAccountAccessor(),
            new CurrentPlatformAdministratorAccessor(), tenantAccessor, new CurrentImpersonationAccessor(),
            systemActor, TimeProvider.System, NullLogger<AuditRecorder>.Instance);

        await recorder.RecordAsync("Document.AttachmentRefused", "Document", Guid.NewGuid(), "tool.bin");

        var recorded = await dbContext.AuditEvents.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(AuditActorType.System, recorded.ActorType);
        Assert.Equal(Guid.Empty, recorded.ActorId);
        Assert.Equal("Inbound mail", recorded.ActorName);
    }
}
