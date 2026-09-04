using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Booking;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// The booking DOCUMENT's lifecycle drives its claim row (ADR 0744: the booking IS the .ics), synced in
// SaveChanges so every path agrees without knowing about bookings: deleting the document cancels the claim,
// restoring it is a REBOOK judged by the same overlap invariant, and moving it between two rooms' Schedules
// re-points the claim at the new room.
public class ResourceBookingSyncTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, Guid? tenantId = null)
    {
        var options = new DbContextOptionsBuilder<SimplArchiveDbContext>()
            .UseSqlite(connection)
            .Options;

        return new SimplArchiveDbContext(options, new CurrentTenantAccessor { TenantId = tenantId });
    }

    private sealed record Fixture(Guid TenantId, Guid RoomId, Guid SecondRoomId, Guid ScheduleId, Guid SecondScheduleId, Guid BookingDocumentId, Guid RowId);

    // Two rooms with their Schedules, and one Active booking: an .ics wearing the REAL RoomBooking mask id
    // (the sync keys on mask identity) inside the first room's Schedule, claimed by its row.
    private static async Task<Fixture> SeedAsync(SqliteConnection connection)
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var roomMaskVersion = Guid.NewGuid();
        var bookingMaskVersion = Guid.NewGuid();
        var f = new Fixture(tenantId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        using var seed = CreateContext(connection);
        seed.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
        seed.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "a@example.com", DisplayName = "A", CreatedAt = DateTimeOffset.UtcNow });
        seed.Masks.Add(new Mask { Id = Guid.NewGuid(), TenantId = tenantId, IsBookable = true, CreatedAt = DateTimeOffset.UtcNow });
        var roomMask = seed.Masks.Local.Single();
        seed.MaskVersions.Add(new MaskVersion { Id = roomMaskVersion, TenantId = tenantId, MaskId = roomMask.Id, Name = "Room", CreatedAt = DateTimeOffset.UtcNow });
        seed.Masks.Add(new Mask { Id = WellKnownMaskIds.RoomBooking, TenantId = tenantId, CreatedAt = DateTimeOffset.UtcNow });
        seed.MaskVersions.Add(new MaskVersion { Id = bookingMaskVersion, TenantId = tenantId, MaskId = WellKnownMaskIds.RoomBooking, Name = "Room booking", CreatedAt = DateTimeOffset.UtcNow });

        seed.Documents.Add(new Document { Id = f.RoomId, TenantId = tenantId, Name = "Room 1", MaskVersionId = roomMaskVersion, CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
        seed.Documents.Add(new Document { Id = f.SecondRoomId, TenantId = tenantId, Name = "Room 2", MaskVersionId = roomMaskVersion, CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
        seed.Documents.Add(new Document { Id = f.ScheduleId, TenantId = tenantId, ParentId = f.RoomId, Name = "Schedule", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
        seed.Documents.Add(new Document { Id = f.SecondScheduleId, TenantId = tenantId, ParentId = f.SecondRoomId, Name = "Schedule", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
        seed.Documents.Add(new Document { Id = f.BookingDocumentId, TenantId = tenantId, ParentId = f.ScheduleId, Name = "Booking", MaskVersionId = bookingMaskVersion, CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
        // The documents first: the bookable-mask check reads STORED rows, so the claim rides a second save.
        await seed.SaveChangesAsync();

        seed.ResourceBookings.Add(new ResourceBooking
        {
            Id = f.RowId,
            TenantId = tenantId,
            ResourceDocumentId = f.RoomId,
            BookingDocumentId = f.BookingDocumentId,
            StartsAtUtc = new DateTimeOffset(2026, 9, 10, 10, 0, 0, TimeSpan.Zero),
            EndsAtUtc = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero),
            Status = BookingStatus.Active,
            BookedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await seed.SaveChangesAsync();
        return f;
    }

    [Fact]
    public async Task Soft_deleting_the_booking_document_cancels_the_claim()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();
        var f = await SeedAsync(connection);

        using (var db = CreateContext(connection, f.TenantId))
        {
            var document = await db.Documents.SingleAsync(d => d.Id == f.BookingDocumentId);
            document.DeletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        using var read = CreateContext(connection, f.TenantId);
        Assert.Equal(BookingStatus.Cancelled, (await read.ResourceBookings.SingleAsync()).Status);
    }

    [Fact]
    public async Task Restoring_it_rebooks_and_a_slot_taken_in_the_meantime_refuses_the_restore()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();
        var f = await SeedAsync(connection);

        using (var db = CreateContext(connection, f.TenantId))
        {
            var document = await db.Documents.SingleAsync(d => d.Id == f.BookingDocumentId);
            document.DeletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        // The free case: restore re-activates the claim through the overlap check.
        using (var db = CreateContext(connection, f.TenantId))
        {
            var document = await db.Documents.IgnoreQueryFilters(["SoftDeleteFilter"]).SingleAsync(d => d.Id == f.BookingDocumentId);
            document.DeletedAt = null;
            await db.SaveChangesAsync();
        }

        using (var read = CreateContext(connection, f.TenantId))
        {
            Assert.Equal(BookingStatus.Active, (await read.ResourceBookings.SingleAsync()).Status);
        }

        // Cancel again, let a rival take the slot, and the restore is REFUSED — not a double booking.
        using (var db = CreateContext(connection, f.TenantId))
        {
            var document = await db.Documents.SingleAsync(d => d.Id == f.BookingDocumentId);
            document.DeletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();

            db.ResourceBookings.Add(new ResourceBooking
            {
                Id = Guid.NewGuid(),
                TenantId = f.TenantId,
                ResourceDocumentId = f.RoomId,
                BookingDocumentId = Guid.NewGuid(),
                StartsAtUtc = new DateTimeOffset(2026, 9, 10, 11, 0, 0, TimeSpan.Zero),
                EndsAtUtc = new DateTimeOffset(2026, 9, 10, 13, 0, 0, TimeSpan.Zero),
                Status = BookingStatus.Active,
                BookedByUserId = (await db.Users.SingleAsync()).Id,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        using (var db = CreateContext(connection, f.TenantId))
        {
            var document = await db.Documents.IgnoreQueryFilters(["SoftDeleteFilter"]).SingleAsync(d => d.Id == f.BookingDocumentId);
            document.DeletedAt = null;
            var refusal = await Assert.ThrowsAsync<BookingInvariantException>(() => db.SaveChangesAsync());
            Assert.Equal(BookingInvariantKind.SlotTaken, refusal.Kind);
        }
    }

    [Fact]
    public async Task Moving_the_booking_to_another_rooms_schedule_repoints_the_claim()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();
        var f = await SeedAsync(connection);

        using (var db = CreateContext(connection, f.TenantId))
        {
            var document = await db.Documents.SingleAsync(d => d.Id == f.BookingDocumentId);
            document.ParentId = f.SecondScheduleId;
            await db.SaveChangesAsync();
        }

        using var read = CreateContext(connection, f.TenantId);
        Assert.Equal(f.SecondRoomId, (await read.ResourceBookings.SingleAsync()).ResourceDocumentId);
    }

    [Fact]
    public async Task Hard_deleting_the_booking_document_cancels_the_claim_and_the_row_survives()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();
        var f = await SeedAsync(connection);

        using (var db = CreateContext(connection, f.TenantId))
        {
            db.Documents.Remove(await db.Documents.SingleAsync(d => d.Id == f.BookingDocumentId));
            await db.SaveChangesAsync();
        }

        // The row is the durable history (the FK is gone by design): it outlives even a purge, Cancelled.
        using var read = CreateContext(connection, f.TenantId);
        var row = await read.ResourceBookings.SingleAsync();
        Assert.Equal(BookingStatus.Cancelled, row.Status);
        Assert.Equal(f.BookingDocumentId, row.BookingDocumentId);
    }
}
