using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// A BOOKABLE mask must be able to hold its Schedule (ADR 0762): the booking primitive's own semantics.
// The static containment table allows the Schedule only under the Meeting room — written for ADR 0744's
// proof, before modules made other masks bookable — so first-booking a bookable MODULE resource (an
// aircraft) died on ItemBelongsElsewhere in the SaveChanges invariant. Found by reading, confirmed here:
// zero Schedules existed in a demo whose aircraft the module declares bookable.
public class BookableScheduleContainmentTests
{
    private static SimplArchiveDbContext Ctx(SqliteConnection c, CurrentTenantAccessor a) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(c).Options, a);

    [Fact]
    public async Task A_schedule_may_be_created_under_a_bookable_module_mask()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var accessor = new CurrentTenantAccessor();
        using (var setup = Ctx(connection, accessor)) await setup.Database.EnsureCreatedAsync();

        Guid aircraftId;
        using (var db = Ctx(connection, accessor))
        {
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "T", CreatedAt = DateTimeOffset.UtcNow });
            db.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "u@t.test", DisplayName = "U", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();

            // The REAL well-known seeder, so the tenant carries the real containment rows (Schedule → Meeting room).
            await new WellKnownMaskSeeder(db, NullLogger<WellKnownMaskSeeder>.Instance).EnsureWellKnownMasksAsync(tenantId);

            // A bookable module-style mask (an aircraft), exactly as the module seeder shapes it.
            var aircraftMask = new Mask { Id = Guid.NewGuid(), TenantId = tenantId, IsFolderMask = true, IsBookable = true, CreatedAt = DateTimeOffset.UtcNow };
            db.Masks.Add(aircraftMask);
            var aircraftVersion = new MaskVersion { Id = Guid.NewGuid(), TenantId = tenantId, MaskId = aircraftMask.Id, Name = "Aircraft", CreatedAt = DateTimeOffset.UtcNow };
            db.MaskVersions.Add(aircraftVersion);
            await db.SaveChangesAsync();

            accessor.TenantId = tenantId;
            aircraftId = Guid.NewGuid();
            db.Documents.Add(new Document { Id = aircraftId, TenantId = tenantId, Name = "HB-PHG", MaskVersionId = aircraftVersion.Id, CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        // First booking creates the Schedule under the aircraft — this is the exact write
        // BookingsController.EnsureScheduleAsync makes, against the real invariant.
        using (var db = Ctx(connection, new CurrentTenantAccessor { TenantId = tenantId }))
        {
            var scheduleVersion = await db.MaskVersions.IgnoreQueryFilters()
                .SingleAsync(v => v.TenantId == tenantId && v.MaskId == WellKnownMaskIds.Schedule && v.IsCurrent);
            db.Documents.Add(new Document
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ParentId = aircraftId,
                Name = "Schedule",
                MaskVersionId = scheduleVersion.Id,
                CreatedByUserId = userId,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(); // must not refuse: the mask is BOOKABLE, and bookable holds a Schedule
        }

        using var check = Ctx(connection, new CurrentTenantAccessor { TenantId = tenantId });
        Assert.Equal(1, await check.Documents.CountAsync(d => d.ParentId == aircraftId));
    }
}
