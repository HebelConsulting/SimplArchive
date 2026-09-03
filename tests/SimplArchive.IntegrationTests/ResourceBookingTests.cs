using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Booking;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// The inventory-booking primitive's SaveChanges invariants (ADR 0735): a booking's resource must wear a
// bookable mask, its slot must have extent, and no two Active bookings of one resource may overlap —
// [start, end) semantics, so back-to-back slots touch without conflict. Enforced at the DbContext because
// bookings are writable from more than one path, and a rule enforced at one entrance is not a rule.
public class ResourceBookingTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, Guid? tenantId = null)
    {
        var options = new DbContextOptionsBuilder<SimplArchiveDbContext>()
            .UseSqlite(connection)
            .Options;

        return new SimplArchiveDbContext(options, new CurrentTenantAccessor { TenantId = tenantId });
    }

    private sealed record Fixture(Guid TenantId, Guid UserId, Guid RoomId, Guid PlainDocumentId);

    // A tenant with a user, a document wearing a BOOKABLE mask (the room), and a plain document with no
    // mask (both a non-bookable target and the booking-payload document).
    private static async Task<Fixture> SeedAsync(SqliteConnection connection)
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var maskId = Guid.NewGuid();
        var maskVersionId = Guid.NewGuid();
        var roomId = Guid.NewGuid();
        var plainId = Guid.NewGuid();

        using var seed = CreateContext(connection);
        seed.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
        seed.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "a@example.com", DisplayName = "A", CreatedAt = DateTimeOffset.UtcNow });
        seed.Masks.Add(new Mask { Id = maskId, TenantId = tenantId, IsBookable = true, CreatedAt = DateTimeOffset.UtcNow });
        seed.MaskVersions.Add(new MaskVersion { Id = maskVersionId, TenantId = tenantId, MaskId = maskId, Name = "Room", CreatedAt = DateTimeOffset.UtcNow });
        seed.Documents.Add(new Document { Id = roomId, TenantId = tenantId, Name = "Room 1", MaskVersionId = maskVersionId, CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
        seed.Documents.Add(new Document { Id = plainId, TenantId = tenantId, Name = "Payload", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
        await seed.SaveChangesAsync();

        return new Fixture(tenantId, userId, roomId, plainId);
    }

    private static ResourceBooking Booking(Fixture f, Guid resourceId, int startHour, int endHour, BookingStatus status = BookingStatus.Active) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = f.TenantId,
        ResourceDocumentId = resourceId,
        BookingDocumentId = f.PlainDocumentId,
        StartsAtUtc = new DateTimeOffset(2026, 9, 10, startHour, 0, 0, TimeSpan.Zero),
        EndsAtUtc = new DateTimeOffset(2026, 9, 10, endHour, 0, 0, TimeSpan.Zero),
        Status = status,
        BookedByUserId = f.UserId,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Overlapping_active_bookings_are_refused()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();
        var f = await SeedAsync(connection);

        using (var first = CreateContext(connection, f.TenantId))
        {
            first.ResourceBookings.Add(Booking(f, f.RoomId, 10, 12));
            await first.SaveChangesAsync();
        }

        using var second = CreateContext(connection, f.TenantId);
        var clash = Booking(f, f.RoomId, 11, 13);
        clash.BookingDocumentId = Guid.NewGuid(); // a different payload document — the conflict is the SLOT
        second.Documents.Add(new Document { Id = clash.BookingDocumentId, TenantId = f.TenantId, Name = "Payload 2", CreatedByUserId = f.UserId, CreatedAt = DateTimeOffset.UtcNow });
        second.ResourceBookings.Add(clash);

        var ex = await Assert.ThrowsAsync<BookingInvariantException>(() => second.SaveChangesAsync());
        // The refusal names the occupied range — a rejection the caller can act on (ADR 0735).
        Assert.Contains("overlaps an existing booking", ex.Message);
    }

    [Fact]
    public async Task Touching_slots_do_not_conflict()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();
        var f = await SeedAsync(connection);

        using (var first = CreateContext(connection, f.TenantId))
        {
            first.ResourceBookings.Add(Booking(f, f.RoomId, 10, 11));
            await first.SaveChangesAsync();
        }

        // [10,11) then [11,12): end is exclusive, so back-to-back bookings are the normal case, not a clash.
        using var second = CreateContext(connection, f.TenantId);
        var next = Booking(f, f.RoomId, 11, 12);
        next.BookingDocumentId = Guid.NewGuid();
        second.Documents.Add(new Document { Id = next.BookingDocumentId, TenantId = f.TenantId, Name = "Payload 2", CreatedByUserId = f.UserId, CreatedAt = DateTimeOffset.UtcNow });
        second.ResourceBookings.Add(next);
        await second.SaveChangesAsync();

        using var check = CreateContext(connection, f.TenantId);
        Assert.Equal(2, await check.ResourceBookings.CountAsync());
    }

    [Fact]
    public async Task A_document_without_a_bookable_mask_is_refused()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();
        var f = await SeedAsync(connection);

        using var context = CreateContext(connection, f.TenantId);
        context.ResourceBookings.Add(Booking(f, f.PlainDocumentId, 10, 12));

        var ex = await Assert.ThrowsAsync<BookingInvariantException>(() => context.SaveChangesAsync());
        Assert.Contains("not a bookable resource", ex.Message);
    }

    [Fact]
    public async Task A_cancelled_booking_does_not_block_the_slot()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();
        var f = await SeedAsync(connection);

        using (var first = CreateContext(connection, f.TenantId))
        {
            first.ResourceBookings.Add(Booking(f, f.RoomId, 10, 12, BookingStatus.Cancelled));
            await first.SaveChangesAsync();
        }

        using var second = CreateContext(connection, f.TenantId);
        var booking = Booking(f, f.RoomId, 10, 12);
        booking.BookingDocumentId = Guid.NewGuid();
        second.Documents.Add(new Document { Id = booking.BookingDocumentId, TenantId = f.TenantId, Name = "Payload 2", CreatedByUserId = f.UserId, CreatedAt = DateTimeOffset.UtcNow });
        second.ResourceBookings.Add(booking);
        await second.SaveChangesAsync();

        using var check = CreateContext(connection, f.TenantId);
        Assert.Equal(2, await check.ResourceBookings.CountAsync());
    }

    [Fact]
    public async Task Two_conflicting_bookings_in_one_save_are_refused()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();
        var f = await SeedAsync(connection);

        // Both rows are only in the ChangeTracker — a stored-rows-only check would let them both through.
        using var context = CreateContext(connection, f.TenantId);
        var a = Booking(f, f.RoomId, 10, 12);
        var b = Booking(f, f.RoomId, 11, 13);
        b.BookingDocumentId = Guid.NewGuid();
        context.Documents.Add(new Document { Id = b.BookingDocumentId, TenantId = f.TenantId, Name = "Payload 2", CreatedByUserId = f.UserId, CreatedAt = DateTimeOffset.UtcNow });
        context.ResourceBookings.AddRange(a, b);

        await Assert.ThrowsAsync<BookingInvariantException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task A_slot_without_extent_is_refused()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();
        var f = await SeedAsync(connection);

        using var context = CreateContext(connection, f.TenantId);
        context.ResourceBookings.Add(Booking(f, f.RoomId, 12, 12));

        var ex = await Assert.ThrowsAsync<BookingInvariantException>(() => context.SaveChangesAsync());
        Assert.Contains("must have extent", ex.Message);
    }

    [Fact]
    public async Task One_booking_row_per_booking_document()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();
        var f = await SeedAsync(connection);

        using (var first = CreateContext(connection, f.TenantId))
        {
            first.ResourceBookings.Add(Booking(f, f.RoomId, 10, 11));
            await first.SaveChangesAsync();
        }

        // Same BookingDocumentId, non-overlapping slot: the unique (TenantId, BookingDocumentId) index
        // refuses it — two claims wearing one justification.
        using var second = CreateContext(connection, f.TenantId);
        second.ResourceBookings.Add(Booking(f, f.RoomId, 14, 15));

        await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task Bookings_of_different_resources_do_not_conflict()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection)) await setup.Database.EnsureCreatedAsync();
        var f = await SeedAsync(connection);

        // A second room, same mask version.
        var room2 = Guid.NewGuid();
        using (var seed = CreateContext(connection, f.TenantId))
        {
            var maskVersionId = await seed.Documents.Where(d => d.Id == f.RoomId).Select(d => d.MaskVersionId).SingleAsync();
            seed.Documents.Add(new Document { Id = room2, TenantId = f.TenantId, Name = "Room 2", MaskVersionId = maskVersionId, CreatedByUserId = f.UserId, CreatedAt = DateTimeOffset.UtcNow });
            await seed.SaveChangesAsync();
        }

        using var context = CreateContext(connection, f.TenantId);
        var a = Booking(f, f.RoomId, 10, 12);
        var b = Booking(f, room2, 10, 12);
        b.BookingDocumentId = Guid.NewGuid();
        context.Documents.Add(new Document { Id = b.BookingDocumentId, TenantId = f.TenantId, Name = "Payload 2", CreatedByUserId = f.UserId, CreatedAt = DateTimeOffset.UtcNow });
        context.ResourceBookings.AddRange(a, b);
        await context.SaveChangesAsync();

        using var check = CreateContext(connection, f.TenantId);
        Assert.Equal(2, await check.ResourceBookings.CountAsync());
    }
}
