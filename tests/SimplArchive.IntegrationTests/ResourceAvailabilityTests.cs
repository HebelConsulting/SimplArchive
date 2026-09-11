using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Booking;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// Offered time (ADR 0780) — the third fact about a resource's timeline, and the one whose rules are defined
// by what it is NOT.
//
// The first test is the reason this table exists at all: a booking must be able to overlap the window it was
// booked into. Had availability been modelled as a claim on the same resource, the no-overlap invariant would
// have refused the first flight anyone booked into an instructor's first published window — on the first
// booking, not as some edge case discovered later.
public class ResourceAvailabilityTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, Guid? tenantId = null) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options,
            new CurrentTenantAccessor { TenantId = tenantId });

    private sealed record Fixture(Guid TenantId, Guid UserId, Guid DossierId, Guid PlainId, Guid WindowDocId, Guid BookingDocId);

    private static async Task<Fixture> SeedAsync(SqliteConnection connection)
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var maskId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var dossierId = Guid.NewGuid();
        var plainId = Guid.NewGuid();
        var windowDocId = Guid.NewGuid();
        var bookingDocId = Guid.NewGuid();

        using var seed = CreateContext(connection);
        seed.Tenants.Add(new Tenant { Id = tenantId, Name = "School", CreatedAt = DateTimeOffset.UtcNow });
        seed.Users.Add(new User
        {
            Id = userId,
            TenantId = tenantId,
            Email = "fi@school.test",
            DisplayName = "Instructor",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        seed.Masks.Add(new Mask
        {
            Id = maskId,
            TenantId = tenantId,
            IsFolderMask = true,
            IsBookable = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        seed.MaskVersions.Add(new MaskVersion
        {
            Id = versionId,
            TenantId = tenantId,
            MaskId = maskId,
            Name = "Pilot dossier",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        seed.Documents.Add(new Document
        {
            Id = dossierId,
            TenantId = tenantId,
            Name = "A. Muster",
            MaskVersionId = versionId,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        seed.Documents.Add(new Document
        {
            Id = plainId,
            TenantId = tenantId,
            Name = "Plain",
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        seed.Documents.Add(new Document
        {
            Id = windowDocId,
            TenantId = tenantId,
            Name = "Thursday",
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        seed.Documents.Add(new Document
        {
            Id = bookingDocId,
            TenantId = tenantId,
            Name = "Training flight",
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await seed.SaveChangesAsync();

        return new Fixture(tenantId, userId, dossierId, plainId, windowDocId, bookingDocId);
    }

    private static DateTimeOffset At(int hour) => new(2027, 4, 8, hour, 0, 0, TimeSpan.Zero);

    private static ResourceAvailability Window(Fixture f, int startHour, int endHour, Guid? documentId = null, AvailabilityStatus status = AvailabilityStatus.Offered) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = f.TenantId,
        ResourceDocumentId = f.DossierId,
        WindowDocumentId = documentId ?? f.WindowDocId,
        StartsAtUtc = At(startHour),
        EndsAtUtc = At(endHour),
        Status = status,
        OfferedByUserId = f.UserId,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static ResourceBooking Booking(Fixture f, int startHour, int endHour) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = f.TenantId,
        ResourceDocumentId = f.DossierId,
        BookingDocumentId = f.BookingDocId,
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

    // THE test. Availability-as-a-claim would have failed here, on the first booking into the first window.
    [Fact]
    public async Task A_booking_may_overlap_the_window_it_was_booked_into()
    {
        var (connection, f) = await StartAsync();
        using var _ = connection;

        using var db = CreateContext(connection, f.TenantId);
        db.ResourceAvailability.Add(Window(f, 9, 17));
        db.ResourceBookings.Add(Booking(f, 11, 12));

        await db.SaveChangesAsync();

        Assert.Equal(1, await db.ResourceAvailability.CountAsync());
        Assert.Equal(1, await db.ResourceBookings.CountAsync());
    }

    // Publishing more time is not a conflict — unlike a second claim, and like a second maintenance block.
    [Fact]
    public async Task Two_windows_of_one_resource_may_overlap()
    {
        var (connection, f) = await StartAsync();
        using var _ = connection;

        using var db = CreateContext(connection, f.TenantId);
        db.ResourceAvailability.Add(Window(f, 9, 17));
        db.ResourceAvailability.Add(Window(f, 14, 20, f.PlainId));

        await db.SaveChangesAsync();

        Assert.Equal(2, await db.ResourceAvailability.CountAsync());
    }

    [Fact]
    public async Task A_window_must_have_extent()
    {
        var (connection, f) = await StartAsync();
        using var _ = connection;

        using var db = CreateContext(connection, f.TenantId);
        db.ResourceAvailability.Add(Window(f, 12, 12));

        var error = await Assert.ThrowsAsync<BookingInvariantException>(() => db.SaveChangesAsync());
        Assert.Equal(BookingInvariantKind.WindowWithoutExtent, error.Kind);
    }

    [Fact]
    public async Task A_window_on_a_non_bookable_document_is_refused()
    {
        var (connection, f) = await StartAsync();
        using var _ = connection;

        using var db = CreateContext(connection, f.TenantId);
        var window = Window(f, 9, 17);
        window.ResourceDocumentId = f.PlainId;
        db.ResourceAvailability.Add(window);

        var error = await Assert.ThrowsAsync<BookingInvariantException>(() => db.SaveChangesAsync());
        Assert.Equal(BookingInvariantKind.NotBookable, error.Kind);
    }

    // COVERS, not overlaps. A window ending at 12:00 does not consent to a flight running until 13:00 merely
    // because the two touch — conflating the tests would let half an offer authorise a whole booking.
    [Theory]
    [InlineData(10, 11, true)]   // wholly inside
    [InlineData(9, 12, true)]    // exactly the window (which is 9-12 below)
    [InlineData(11, 13, false)]  // runs past the end
    [InlineData(8, 10, false)]   // starts before the beginning
    [InlineData(18, 19, false)]  // outside entirely
    public async Task A_window_authorises_only_a_slot_it_covers_entirely(int startHour, int endHour, bool covered)
    {
        var (connection, f) = await StartAsync();
        using var _ = connection;

        using (var offering = CreateContext(connection, f.TenantId))
        {
            offering.ResourceAvailability.Add(Window(f, 9, 12));
            await offering.SaveChangesAsync();
        }

        using var db = CreateContext(connection, f.TenantId);
        var windows = await db.WindowsCoveringAsync(f.TenantId, f.DossierId, At(startHour), At(endHour), CancellationToken.None);

        Assert.Equal(covered, windows.Count > 0);
    }

    // Two adjacent windows arguably cover the slot between them, but treating them as one offer is an
    // inference about somebody's intent the core should not make — a resource offering continuous time can
    // publish it as one window.
    [Fact]
    public async Task Two_adjacent_windows_do_not_jointly_cover_a_slot_spanning_both()
    {
        var (connection, f) = await StartAsync();
        using var _ = connection;

        using (var offering = CreateContext(connection, f.TenantId))
        {
            offering.ResourceAvailability.Add(Window(f, 9, 12));
            offering.ResourceAvailability.Add(Window(f, 12, 17, f.PlainId));
            await offering.SaveChangesAsync();
        }

        using var db = CreateContext(connection, f.TenantId);
        var windows = await db.WindowsCoveringAsync(f.TenantId, f.DossierId, At(11), At(13), CancellationToken.None);

        Assert.Empty(windows);
    }

    [Fact]
    public async Task A_withdrawn_window_offers_nothing()
    {
        var (connection, f) = await StartAsync();
        using var _ = connection;

        using (var offering = CreateContext(connection, f.TenantId))
        {
            offering.ResourceAvailability.Add(Window(f, 9, 17, status: AvailabilityStatus.Withdrawn));
            await offering.SaveChangesAsync();
        }

        using var db = CreateContext(connection, f.TenantId);
        Assert.Empty(await db.WindowsCoveringAsync(f.TenantId, f.DossierId, At(10), At(11), CancellationToken.None));
    }

    // The document's lifecycle drives the row, as it does for a booking and a block: deleting the .ics
    // withdraws the offer on every delete path without any of them knowing what availability is.
    [Fact]
    public async Task Soft_deleting_the_window_document_withdraws_the_offer()
    {
        var (connection, f) = await StartAsync();
        using var _ = connection;

        var maskVersionId = Guid.NewGuid();
        using (var setup = CreateContext(connection, f.TenantId))
        {
            setup.Masks.Add(new Mask
            {
                Id = WellKnownMaskIds.AvailabilityWindow,
                TenantId = f.TenantId,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            setup.MaskVersions.Add(new MaskVersion
            {
                Id = maskVersionId,
                TenantId = f.TenantId,
                MaskId = WellKnownMaskIds.AvailabilityWindow,
                Name = "Availability window",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await setup.SaveChangesAsync();

            var document = await setup.Documents.SingleAsync(d => d.Id == f.WindowDocId);
            document.MaskVersionId = maskVersionId;
            setup.ResourceAvailability.Add(Window(f, 9, 17));
            await setup.SaveChangesAsync();
        }

        using (var deleting = CreateContext(connection, f.TenantId))
        {
            var document = await deleting.Documents.SingleAsync(d => d.Id == f.WindowDocId);
            document.DeletedAt = DateTimeOffset.UtcNow;
            await deleting.SaveChangesAsync();
        }

        using var db = CreateContext(connection, f.TenantId);
        Assert.Equal(AvailabilityStatus.Withdrawn, (await db.ResourceAvailability.SingleAsync()).Status);
        Assert.Empty(await db.WindowsCoveringAsync(f.TenantId, f.DossierId, At(10), At(11), CancellationToken.None));
    }
}
