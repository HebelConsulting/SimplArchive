using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Booking;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// The maintenance-block half of the booking primitive (ADR 0778). A block is the booking's mirror image and
// the asymmetries are the whole design, so they are what these tests pin: blocks MAY overlap each other,
// while a booking may not overlap a block at all.
//
// Suspension is DERIVED (#1091) — a booking is suspended exactly while an Active block of its resource
// overlaps it — so there is deliberately no "suspend" or "revive" state to test. What is tested instead is
// that clearing a block frees what it caught with nothing having been written, which is the property the
// derived design exists to guarantee.
public class ResourceBlockTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, Guid? tenantId = null) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options,
            new CurrentTenantAccessor { TenantId = tenantId });

    private sealed record Fixture(Guid TenantId, Guid UserId, Guid RoomId, Guid BookingDocumentId, Guid BlockDocumentId, Guid PlainDocumentId);

    private static async Task<Fixture> SeedAsync(SqliteConnection connection)
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var maskId = Guid.NewGuid();
        var maskVersionId = Guid.NewGuid();
        var roomId = Guid.NewGuid();
        var bookingDocId = Guid.NewGuid();
        var blockDocId = Guid.NewGuid();
        var plainId = Guid.NewGuid();

        using var seed = CreateContext(connection);
        seed.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
        seed.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "a@example.com", DisplayName = "A", CreatedAt = DateTimeOffset.UtcNow });
        seed.Masks.Add(new Mask { Id = maskId, TenantId = tenantId, IsBookable = true, CreatedAt = DateTimeOffset.UtcNow });
        seed.MaskVersions.Add(new MaskVersion { Id = maskVersionId, TenantId = tenantId, MaskId = maskId, Name = "Aircraft", CreatedAt = DateTimeOffset.UtcNow });
        seed.Documents.Add(new Document { Id = roomId, TenantId = tenantId, Name = "HB-XYZ", MaskVersionId = maskVersionId, CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
        seed.Documents.Add(new Document { Id = bookingDocId, TenantId = tenantId, Name = "Flight", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
        seed.Documents.Add(new Document { Id = blockDocId, TenantId = tenantId, Name = "50h check", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
        seed.Documents.Add(new Document { Id = plainId, TenantId = tenantId, Name = "Plain", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
        await seed.SaveChangesAsync();

        return new Fixture(tenantId, userId, roomId, bookingDocId, blockDocId, plainId);
    }

    private static DateTimeOffset At(int hour) => new(2026, 9, 10, hour, 0, 0, TimeSpan.Zero);

    private static ResourceBlock Block(Fixture f, int startHour, int endHour, Guid? documentId = null, BlockStatus status = BlockStatus.Active) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = f.TenantId,
        ResourceDocumentId = f.RoomId,
        BlockDocumentId = documentId ?? f.BlockDocumentId,
        StartsAtUtc = At(startHour),
        EndsAtUtc = At(endHour),
        Status = status,
        BlockedByUserId = f.UserId,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static ResourceBooking Booking(Fixture f, int startHour, int endHour, Guid? documentId = null) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = f.TenantId,
        ResourceDocumentId = f.RoomId,
        BookingDocumentId = documentId ?? f.BookingDocumentId,
        StartsAtUtc = At(startHour),
        EndsAtUtc = At(endHour),
        Status = BookingStatus.Active,
        BookedByUserId = f.UserId,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static async Task<(SqliteConnection Connection, Fixture Fixture)> StartAsync()
    {
        var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = CreateContext(connection))
        {
            await setup.Database.EnsureCreatedAsync();
        }

        return (connection, await SeedAsync(connection));
    }

    // The asymmetry that gives blocks their own table: two defects reported on one aircraft are two findings,
    // and refusing the second would lose one. A flag on ResourceBooking would have inherited the no-overlap
    // rule and made this impossible.
    [Fact]
    public async Task Two_blocks_of_one_resource_may_overlap()
    {
        var (connection, f) = await StartAsync();
        using var _ = connection;

        using var db = CreateContext(connection, f.TenantId);
        db.ResourceBlocks.Add(Block(f, 9, 12));
        db.ResourceBlocks.Add(Block(f, 10, 14, f.PlainDocumentId));

        await db.SaveChangesAsync();

        Assert.Equal(2, await db.ResourceBlocks.CountAsync(b => b.Status == BlockStatus.Active));
    }

    [Fact]
    public async Task A_booking_inside_a_block_is_refused()
    {
        var (connection, f) = await StartAsync();
        using var _ = connection;

        using (var blocking = CreateContext(connection, f.TenantId))
        {
            blocking.ResourceBlocks.Add(Block(f, 9, 17));
            await blocking.SaveChangesAsync();
        }

        using var db = CreateContext(connection, f.TenantId);
        db.ResourceBookings.Add(Booking(f, 10, 11));

        var error = await Assert.ThrowsAsync<BookingInvariantException>(() => db.SaveChangesAsync());

        // By KIND, never by message text: a block and a taken slot are different facts with different
        // remedies, and a caller that cannot tell them apart sends the pilot hunting for a free hour that
        // does not exist.
        Assert.Equal(BookingInvariantKind.ResourceBlocked, error.Kind);
    }

    // Half-open [start, end) on BOTH sides, the same semantics bookings already use against each other — so
    // the aircraft is available again the instant the block ends.
    [Fact]
    public async Task A_booking_starting_exactly_when_a_block_ends_is_allowed()
    {
        var (connection, f) = await StartAsync();
        using var _ = connection;

        using (var blocking = CreateContext(connection, f.TenantId))
        {
            blocking.ResourceBlocks.Add(Block(f, 9, 12));
            await blocking.SaveChangesAsync();
        }

        using var db = CreateContext(connection, f.TenantId);
        db.ResourceBookings.Add(Booking(f, 12, 13));

        await db.SaveChangesAsync();

        Assert.Equal(1, await db.ResourceBookings.CountAsync());
    }

    // A CLEARED block withdraws nothing. This is what makes revival free: the block stays as history, and the
    // booking is bookable again with no row having been rewritten.
    [Fact]
    public async Task A_cleared_block_does_not_refuse_a_booking()
    {
        var (connection, f) = await StartAsync();
        using var _ = connection;

        using (var blocking = CreateContext(connection, f.TenantId))
        {
            blocking.ResourceBlocks.Add(Block(f, 9, 17, status: BlockStatus.Cleared));
            await blocking.SaveChangesAsync();
        }

        using var db = CreateContext(connection, f.TenantId);
        db.ResourceBookings.Add(Booking(f, 10, 11));

        await db.SaveChangesAsync();

        Assert.Equal(1, await db.ResourceBookings.CountAsync());
    }

    // A booking that ALREADY exists is not refused when a block lands on it — it is suspended, which is a
    // derived fact and therefore nothing the save has to write. This is the pair to the refusal above: the
    // same overlap, judged on opposite sides of the block's placement, and only one of them is an error.
    [Fact]
    public async Task A_block_placed_over_an_existing_booking_is_accepted_and_leaves_the_booking_active()
    {
        var (connection, f) = await StartAsync();
        using var _ = connection;

        using (var booking = CreateContext(connection, f.TenantId))
        {
            booking.ResourceBookings.Add(Booking(f, 10, 11));
            await booking.SaveChangesAsync();
        }

        using var db = CreateContext(connection, f.TenantId);
        db.ResourceBlocks.Add(Block(f, 9, 17));
        await db.SaveChangesAsync();

        // Still Active: it holds its slot, so nobody else may take it. "May it be flown" is the separate,
        // derived question — collapsing the two would silently free the aircraft in every existing caller.
        var stored = await db.ResourceBookings.SingleAsync();
        Assert.Equal(BookingStatus.Active, stored.Status);
    }

    [Fact]
    public async Task A_block_on_a_non_bookable_document_is_refused()
    {
        var (connection, f) = await StartAsync();
        using var _ = connection;

        using var db = CreateContext(connection, f.TenantId);
        var block = Block(f, 9, 12);
        block.ResourceDocumentId = f.PlainDocumentId;
        db.ResourceBlocks.Add(block);

        var error = await Assert.ThrowsAsync<BookingInvariantException>(() => db.SaveChangesAsync());
        Assert.Equal(BookingInvariantKind.NotBookable, error.Kind);
    }

    [Fact]
    public async Task A_block_window_must_have_extent()
    {
        var (connection, f) = await StartAsync();
        using var _ = connection;

        using var db = CreateContext(connection, f.TenantId);
        db.ResourceBlocks.Add(Block(f, 12, 12));

        var error = await Assert.ThrowsAsync<BookingInvariantException>(() => db.SaveChangesAsync());
        Assert.Equal(BookingInvariantKind.BlockWithoutExtent, error.Kind);
    }

    // The document's lifecycle drives the row, exactly as a booking's does — so deleting the .ics clears the
    // block on every delete path (API, recycle bin, CalDAV DELETE) without any of them knowing what a block
    // is. This is the revival: no pass over the bookings, nothing to miss.
    [Fact]
    public async Task Soft_deleting_the_block_document_clears_the_block_and_frees_the_window()
    {
        var (connection, f) = await StartAsync();
        using var _ = connection;

        var maskVersionId = Guid.NewGuid();
        using (var setup = CreateContext(connection, f.TenantId))
        {
            // The sync keys on the document wearing the MaintenanceBlock mask, so the fixture has to give it
            // one — the classifier stamps it on the real path.
            var maskId = WellKnownMaskIds.MaintenanceBlock;
            setup.Masks.Add(new Mask { Id = maskId, TenantId = f.TenantId, CreatedAt = DateTimeOffset.UtcNow });
            setup.MaskVersions.Add(new MaskVersion
            {
                Id = maskVersionId,
                TenantId = f.TenantId,
                MaskId = maskId,
                Name = "Maintenance block",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await setup.SaveChangesAsync();

            var document = await setup.Documents.SingleAsync(d => d.Id == f.BlockDocumentId);
            document.MaskVersionId = maskVersionId;
            setup.ResourceBlocks.Add(Block(f, 9, 17));
            await setup.SaveChangesAsync();
        }

        using (var deleting = CreateContext(connection, f.TenantId))
        {
            var document = await deleting.Documents.SingleAsync(d => d.Id == f.BlockDocumentId);
            document.DeletedAt = DateTimeOffset.UtcNow;
            await deleting.SaveChangesAsync();
        }

        using var db = CreateContext(connection, f.TenantId);
        Assert.Equal(BlockStatus.Cleared, (await db.ResourceBlocks.SingleAsync()).Status);

        // ...and the window is bookable again, which is the point of clearing it.
        db.ResourceBookings.Add(Booking(f, 10, 11));
        await db.SaveChangesAsync();
        Assert.Equal(1, await db.ResourceBookings.CountAsync());
    }
}
