using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Api.Modules;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Notifications;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// Telling a TENANT ADMINISTRATOR that a module's content has stopped refreshing (ADR 0811).
//
// The gap: a populate hook degrades to "serve what is already filed" when its fetch fails — deliberately,
// because a failed enrichment must not break somebody else's read. The price was silence. The only record
// was a log line in the OPERATOR's collector, which a tenant administrator has no access to, so a dead
// weather feed looked exactly like a quiet one, indefinitely, inside an installation where every other check
// was green.
//
// What these pin is the THRESHOLD behaviour, because that is the part where a plausible implementation is
// wrong in a way nobody notices: notifying per failure is a storm that arrives exactly when a provider is
// already having a bad day, and notifying repeatedly after the threshold is the same storm delayed.
public class ModuleContentHealthTests
{
    private const string Module = "test-module";
    private const string Machine = "test-pilot";

    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, Guid? tenantId = null) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options,
            new CurrentTenantAccessor { TenantId = tenantId });

    private sealed record Rig(SimplArchiveDbContext Context, ModuleContentHealthRecorder Recorder, Guid TenantId, Guid FolderId, Guid AdminId);

    private static async Task<Rig> RigAsync(SqliteConnection connection, int admins = 1, bool adminActive = true)
    {
        using (var setup = CreateContext(connection))
        {
            await setup.Database.EnsureCreatedAsync();
        }

        var tenantId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        using (var seed = CreateContext(connection))
        {
            seed.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });

            // A REAL document, because the notification carries DocumentId as a foreign key so it can open
            // the folder it is about. A rig that passed a bare Guid produced a silent FK violation inside the
            // recorder's save — which is exactly the swallow that catch now logs.
            seed.Documents.Add(new SimplArchive.Domain.Documents.Document
            {
                Id = folderId,
                TenantId = tenantId,
                Name = "LSZH",
                CreatedByUserId = adminId,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            for (var i = 0; i < admins; i++)
            {
                seed.Users.Add(new User
                {
                    Id = i == 0 ? adminId : Guid.NewGuid(),
                    TenantId = tenantId,
                    Email = $"admin{i}@example.com",
                    DisplayName = $"Admin {i}",
                    IsTenantAdmin = true,
                    IsActive = adminActive,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            }

            // A non-admin, to prove the fan-out is to ADMINISTRATORS and not to everybody: a content feed
            // being down is an operational fact its users can do nothing about.
            seed.Users.Add(new User
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Email = "user@example.com",
                DisplayName = "User",
                IsTenantAdmin = false,
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        var context = CreateContext(connection, tenantId);
        return new Rig(context, new ModuleContentHealthRecorder(context, NullLogger<ModuleContentHealthRecorder>.Instance),
            tenantId, folderId, adminId);
    }

    private static Task FailAsync(Rig rig, string error = "the provider answered 503") =>
        rig.Recorder.RecordFailureAsync(rig.TenantId, Module, Machine, rig.FolderId, "LSZH", error, CancellationToken.None);

    private static Task<int> NotificationCountAsync(Rig rig) =>
        rig.Context.Notifications.IgnoreQueryFilters()
            .CountAsync(n => n.Type == NotificationType.ModuleContentFailing);

    [Fact]
    public async Task Below_the_threshold_nobody_is_told()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var rig = await RigAsync(connection);

        for (var i = 0; i < ModuleContentHealthRecorder.NotifyAfterConsecutiveFailures - 1; i++)
        {
            await FailAsync(rig);
        }

        Assert.Equal(0, await NotificationCountAsync(rig));

        // …but the state is already recorded. That is the whole two-layer point: an administrator who goes
        // looking can see a source is struggling before it is bad enough to interrupt them about.
        var row = await rig.Context.ModuleContentHealth.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(ModuleContentHealthRecorder.NotifyAfterConsecutiveFailures - 1, row.ConsecutiveFailures);
        Assert.Null(row.NotifiedAt);
    }

    [Fact]
    public async Task Crossing_the_threshold_tells_every_active_admin_once()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var rig = await RigAsync(connection, admins: 3);

        for (var i = 0; i < ModuleContentHealthRecorder.NotifyAfterConsecutiveFailures; i++)
        {
            await FailAsync(rig);
        }

        Assert.Equal(3, await NotificationCountAsync(rig));

        // The folder rides along, so the notification opens the thing it is about rather than leaving an
        // administrator to find a name out of a sentence.
        var note = await rig.Context.Notifications.IgnoreQueryFilters()
            .FirstAsync(n => n.Type == NotificationType.ModuleContentFailing);
        Assert.Equal(rig.FolderId, note.DocumentId);
        Assert.Contains("LSZH", note.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Staying_broken_does_not_notify_again()
    {
        // The failure mode a threshold alone does not prevent: a hook runs on every folder open, so an
        // administrator who did not fix it in the first hour would otherwise be told again on every open for
        // as long as it stays broken — which is the storm, arriving late.
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var rig = await RigAsync(connection);

        for (var i = 0; i < ModuleContentHealthRecorder.NotifyAfterConsecutiveFailures + 12; i++)
        {
            await FailAsync(rig);
        }

        Assert.Equal(1, await NotificationCountAsync(rig));
        var row = await rig.Context.ModuleContentHealth.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(ModuleContentHealthRecorder.NotifyAfterConsecutiveFailures + 12, row.ConsecutiveFailures);
    }

    [Fact]
    public async Task A_success_clears_the_episode_and_a_new_one_notifies_again()
    {
        // Absence is how "healthy" is stored, so recovery must DELETE rather than zero — otherwise a stale
        // zero row sits there meaning the same thing a missing one does, and the next episode has to reason
        // about which it is looking at.
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var rig = await RigAsync(connection);

        for (var i = 0; i < ModuleContentHealthRecorder.NotifyAfterConsecutiveFailures; i++)
        {
            await FailAsync(rig);
        }

        await rig.Recorder.RecordSuccessAsync(rig.TenantId, Module, Machine, rig.FolderId, CancellationToken.None);
        Assert.Empty(await rig.Context.ModuleContentHealth.IgnoreQueryFilters().ToListAsync());

        for (var i = 0; i < ModuleContentHealthRecorder.NotifyAfterConsecutiveFailures; i++)
        {
            await FailAsync(rig);
        }

        Assert.Equal(2, await NotificationCountAsync(rig));
    }

    [Fact]
    public async Task Two_sources_are_counted_apart()
    {
        // The reason the row is per SOURCE and not per module, and it is the whole design rather than a
        // detail: counting per module lets one permanently-broken source hide behind its healthy siblings —
        // every success on a different folder would reset the shared counter, and "three consecutive" would
        // never arrive for a feed that has been dead for a week.
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var rig = await RigAsync(connection);
        var other = Guid.NewGuid();

        for (var i = 0; i < ModuleContentHealthRecorder.NotifyAfterConsecutiveFailures; i++)
        {
            await FailAsync(rig);
            await rig.Recorder.RecordSuccessAsync(rig.TenantId, Module, Machine, other, CancellationToken.None);
        }

        Assert.Equal(1, await NotificationCountAsync(rig));
    }

    [Fact]
    public async Task A_success_on_one_source_does_not_clear_another()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var rig = await RigAsync(connection);
        var other = Guid.NewGuid();

        await FailAsync(rig);
        await rig.Recorder.RecordSuccessAsync(rig.TenantId, Module, Machine, other, CancellationToken.None);

        var row = await rig.Context.ModuleContentHealth.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(rig.FolderId, row.SubjectDocumentId);
        Assert.Equal(1, row.ConsecutiveFailures);
    }

    [Fact]
    public async Task A_deactivated_admin_is_not_notified()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var rig = await RigAsync(connection, adminActive: false);

        for (var i = 0; i < ModuleContentHealthRecorder.NotifyAfterConsecutiveFailures; i++)
        {
            await FailAsync(rig);
        }

        Assert.Equal(0, await NotificationCountAsync(rig));

        // The episode is still recorded, so the state survives for whoever is made an administrator next —
        // a tenant with nobody to tell is a finding, not a reason to forget.
        Assert.Single(await rig.Context.ModuleContentHealth.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task A_verbose_provider_cannot_own_the_status_line()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var rig = await RigAsync(connection);

        await FailAsync(rig, new string('x', 5000));

        var row = await rig.Context.ModuleContentHealth.IgnoreQueryFilters().SingleAsync();
        Assert.True(row.LastError.Length <= 500,
            $"LastError is {row.LastError.Length} chars; it is rendered into an administrator's status line "
            + "and the column caps at 500, so an uncapped write would fail the save instead.");
    }
}
