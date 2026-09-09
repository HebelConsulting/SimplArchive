using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Booking;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// One booking, several claims (ADR 0774): a training flight occupies the aircraft, the student and the
// instructor for one window, and there is no second document to write.
//
// This overturns a deliberate invariant — "one booking row per booking document; two rows would be two
// claims wearing one justification" — which held for exactly as long as a booking meant one resource. What
// replaces it is pinned here: the same resource may not be claimed twice by one booking (still an index),
// and every claim of one booking names the SAME window (now an invariant, because the index no longer makes
// it true by construction).
public class MultiResourceBookingTests
{
    private static SimplArchiveDbContext Ctx(SqliteConnection connection, Guid? tenantId = null) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options,
            new CurrentTenantAccessor { TenantId = tenantId });

    private sealed record Fixture(
        Guid TenantId, Guid UserId, Guid AircraftId, Guid SecondAircraftId, Guid StudentId, Guid InstructorId,
        Guid ScheduleId, Guid SecondScheduleId, Guid FlightId);

    private static readonly DateTimeOffset Start = new(2026, 9, 10, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Two aircraft with Schedules, two people, and the flight's .ics in the first aircraft's
    /// Schedule. Everything bookable: the dossier holds a person's time exactly as an airframe holds its
    /// own (#1091), so both wear a bookable mask.</summary>
    private static async Task<Fixture> SeedAsync(SqliteConnection connection)
    {
        var f = new Fixture(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var bookableMaskId = Guid.NewGuid();
        var bookableVersion = Guid.NewGuid();
        var bookingVersion = Guid.NewGuid();

        using var seed = Ctx(connection);
        seed.Tenants.Add(new Tenant { Id = f.TenantId, Name = $"School {f.TenantId}", CreatedAt = DateTimeOffset.UtcNow });
        seed.Users.Add(new User { Id = f.UserId, TenantId = f.TenantId, Email = "a@example.com", DisplayName = "A", CreatedAt = DateTimeOffset.UtcNow });
        seed.Masks.Add(new Mask { Id = bookableMaskId, TenantId = f.TenantId, IsBookable = true, CreatedAt = DateTimeOffset.UtcNow });
        seed.MaskVersions.Add(new MaskVersion { Id = bookableVersion, TenantId = f.TenantId, MaskId = bookableMaskId, Name = "Bookable", CreatedAt = DateTimeOffset.UtcNow });
        seed.Masks.Add(new Mask { Id = WellKnownMaskIds.Booking, TenantId = f.TenantId, CreatedAt = DateTimeOffset.UtcNow });
        seed.MaskVersions.Add(new MaskVersion { Id = bookingVersion, TenantId = f.TenantId, MaskId = WellKnownMaskIds.Booking, Name = "Booking", CreatedAt = DateTimeOffset.UtcNow });

        void Doc(Guid id, Guid? parent, string name, Guid? maskVersion) =>
            seed.Documents.Add(new Document
            {
                Id = id,
                TenantId = f.TenantId,
                ParentId = parent,
                Name = name,
                MaskVersionId = maskVersion,
                CreatedByUserId = f.UserId,
                CreatedAt = DateTimeOffset.UtcNow,
            });

        Doc(f.AircraftId, null, "HB-PHG", bookableVersion);
        Doc(f.SecondAircraftId, null, "HB-KEL", bookableVersion);
        Doc(f.StudentId, null, "Student", bookableVersion);
        Doc(f.InstructorId, null, "Instructor", bookableVersion);
        Doc(f.ScheduleId, f.AircraftId, "Schedule", null);
        Doc(f.SecondScheduleId, f.SecondAircraftId, "Schedule", null);
        Doc(f.FlightId, f.ScheduleId, "Flight", bookingVersion);

        // Documents first: the bookable-mask check reads STORED rows, so the claims ride a second save.
        await seed.SaveChangesAsync();
        return f;
    }

    private static ResourceBooking Claim(Fixture f, Guid resourceId, DateTimeOffset? start = null, DateTimeOffset? end = null, Guid? documentId = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            TenantId = f.TenantId,
            ResourceDocumentId = resourceId,
            BookingDocumentId = documentId ?? f.FlightId,
            StartsAtUtc = start ?? Start,
            EndsAtUtc = end ?? End,
            Status = BookingStatus.Active,
            BookedByUserId = f.UserId,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    private static async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using var setup = Ctx(connection);
        await setup.Database.EnsureCreatedAsync();
        return connection;
    }

    [Fact]
    public async Task One_booking_may_claim_the_aircraft_the_student_and_the_instructor()
    {
        using var connection = await OpenAsync();
        var f = await SeedAsync(connection);

        using (var context = Ctx(connection, f.TenantId))
        {
            context.ResourceBookings.AddRange(
                Claim(f, f.AircraftId), Claim(f, f.StudentId), Claim(f, f.InstructorId));
            await context.SaveChangesAsync();
        }

        using (var context = Ctx(connection, f.TenantId))
        {
            Assert.Equal(3, await context.ResourceBookings.CountAsync(b => b.BookingDocumentId == f.FlightId));
        }
    }

    // The same resource claimed twice by one booking is a duplicate, not a second participant — and it is
    // refused by the OVERLAP invariant before the widened index ever sees it, because two claims on one
    // resource for one window overlap each other.
    //
    // Written the other way round first, expecting the index to bite. That expectation was wrong, and the
    // truth is better: the invariant names the clashing window, while a unique-index violation would surface
    // as a bare DbUpdateException. The index stays as the last line for writes that never pass through
    // SaveChanges at all — the same "database as the backstop" split the resource-delete rule already uses.
    [Fact]
    public async Task One_booking_may_not_claim_the_same_resource_twice()
    {
        using var connection = await OpenAsync();
        var f = await SeedAsync(connection);

        using var context = Ctx(connection, f.TenantId);
        context.ResourceBookings.AddRange(Claim(f, f.AircraftId), Claim(f, f.AircraftId));

        var error = await Assert.ThrowsAsync<BookingInvariantException>(() => context.SaveChangesAsync());
        Assert.Equal(BookingInvariantKind.SlotTaken, error.Kind);
    }

    // The invariant that replaces the dropped uniqueness. Without it three rows could drift apart and the
    // document would describe a booking none of its own claims agreed with.
    [Fact]
    public async Task Claims_of_one_booking_must_name_the_same_window()
    {
        using var connection = await OpenAsync();
        var f = await SeedAsync(connection);

        using var context = Ctx(connection, f.TenantId);
        context.ResourceBookings.AddRange(
            Claim(f, f.AircraftId),
            Claim(f, f.InstructorId, start: Start.AddHours(1), end: End.AddHours(1)));

        var error = await Assert.ThrowsAsync<BookingInvariantException>(() => context.SaveChangesAsync());
        Assert.Equal(BookingInvariantKind.ClaimsDisagreeOnSlot, error.Kind);
    }

    // Judged against tracked state as well as stored rows: a claim added later must agree with claims that
    // are already persisted, not only with its own batch.
    [Fact]
    public async Task A_claim_added_later_must_agree_with_the_stored_ones()
    {
        using var connection = await OpenAsync();
        var f = await SeedAsync(connection);

        using (var context = Ctx(connection, f.TenantId))
        {
            context.ResourceBookings.Add(Claim(f, f.AircraftId));
            await context.SaveChangesAsync();
        }

        using (var context = Ctx(connection, f.TenantId))
        {
            context.ResourceBookings.Add(Claim(f, f.InstructorId, start: Start.AddHours(3), end: End.AddHours(3)));
            var error = await Assert.ThrowsAsync<BookingInvariantException>(() => context.SaveChangesAsync());
            Assert.Equal(BookingInvariantKind.ClaimsDisagreeOnSlot, error.Kind);
        }
    }

    // The overlap invariant is per RESOURCE and needed no change — which is worth pinning, because it is
    // what makes "an instructor cannot teach two students at once" the same rule as "an aircraft cannot be
    // in two places at once", rather than a second mechanism for people.
    [Fact]
    public async Task An_instructor_cannot_be_claimed_by_two_flights_at_once()
    {
        using var connection = await OpenAsync();
        var f = await SeedAsync(connection);
        var secondFlight = Guid.NewGuid();

        using (var context = Ctx(connection, f.TenantId))
        {
            context.Documents.Add(new Document
            {
                Id = secondFlight,
                TenantId = f.TenantId,
                ParentId = f.SecondScheduleId,
                Name = "Flight 2",
                MaskVersionId = (await context.Documents.SingleAsync(d => d.Id == f.FlightId)).MaskVersionId,
                CreatedByUserId = f.UserId,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            context.ResourceBookings.AddRange(Claim(f, f.AircraftId), Claim(f, f.InstructorId));
            await context.SaveChangesAsync();
        }

        using (var context = Ctx(connection, f.TenantId))
        {
            // A different aircraft, the same hour, the same instructor.
            context.ResourceBookings.Add(Claim(f, f.SecondAircraftId, documentId: secondFlight));
            context.ResourceBookings.Add(Claim(f, f.InstructorId, documentId: secondFlight));

            var error = await Assert.ThrowsAsync<BookingInvariantException>(() => context.SaveChangesAsync());
            Assert.Equal(BookingInvariantKind.SlotTaken, error.Kind);
        }
    }

    [Fact]
    public async Task Cancelling_the_document_cancels_every_claim()
    {
        using var connection = await OpenAsync();
        var f = await SeedAsync(connection);

        using (var context = Ctx(connection, f.TenantId))
        {
            context.ResourceBookings.AddRange(Claim(f, f.AircraftId), Claim(f, f.StudentId), Claim(f, f.InstructorId));
            await context.SaveChangesAsync();
        }

        using (var context = Ctx(connection, f.TenantId))
        {
            (await context.Documents.SingleAsync(d => d.Id == f.FlightId)).DeletedAt = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync();
        }

        using (var context = Ctx(connection, f.TenantId))
        {
            // Freeing the aircraft while leaving two people committed to a flight that no longer exists is
            // exactly what cancelling only the first row would have done.
            var rows = await context.ResourceBookings.Where(b => b.BookingDocumentId == f.FlightId).ToListAsync();
            Assert.Equal(3, rows.Count);
            Assert.All(rows, r => Assert.Equal(BookingStatus.Cancelled, r.Status));
        }
    }

    [Fact]
    public async Task Restoring_the_document_reactivates_every_claim()
    {
        using var connection = await OpenAsync();
        var f = await SeedAsync(connection);

        using (var context = Ctx(connection, f.TenantId))
        {
            context.ResourceBookings.AddRange(Claim(f, f.AircraftId), Claim(f, f.StudentId), Claim(f, f.InstructorId));
            await context.SaveChangesAsync();
        }

        using (var context = Ctx(connection, f.TenantId))
        {
            (await context.Documents.SingleAsync(d => d.Id == f.FlightId)).DeletedAt = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync();
        }

        using (var context = Ctx(connection, f.TenantId))
        {
            var restored = await context.Documents.IgnoreQueryFilters(["SoftDeleteFilter"]).SingleAsync(d => d.Id == f.FlightId);
            restored.DeletedAt = null;
            await context.SaveChangesAsync();
        }

        using (var context = Ctx(connection, f.TenantId))
        {
            var rows = await context.ResourceBookings.Where(b => b.BookingDocumentId == f.FlightId).ToListAsync();
            Assert.All(rows, r => Assert.Equal(BookingStatus.Active, r.Status));
        }
    }

    // Moving the .ics between two aircraft Schedules changes which AIRCRAFT is flown and says nothing about
    // who is flying it. Re-pointing every claim would silently reassign the people to the new aircraft as if
    // they were rooms — which is what the single-claim code would have done once a second claim existed.
    [Fact]
    public async Task Moving_the_booking_repoints_only_the_claim_on_the_holding_resource()
    {
        using var connection = await OpenAsync();
        var f = await SeedAsync(connection);

        using (var context = Ctx(connection, f.TenantId))
        {
            context.ResourceBookings.AddRange(Claim(f, f.AircraftId), Claim(f, f.StudentId), Claim(f, f.InstructorId));
            await context.SaveChangesAsync();
        }

        using (var context = Ctx(connection, f.TenantId))
        {
            (await context.Documents.SingleAsync(d => d.Id == f.FlightId)).ParentId = f.SecondScheduleId;
            await context.SaveChangesAsync();
        }

        using (var context = Ctx(connection, f.TenantId))
        {
            var resources = await context.ResourceBookings
                .Where(b => b.BookingDocumentId == f.FlightId)
                .Select(b => b.ResourceDocumentId)
                .ToListAsync();

            Assert.Contains(f.SecondAircraftId, resources);   // the aircraft claim followed the move
            Assert.DoesNotContain(f.AircraftId, resources);
            Assert.Contains(f.StudentId, resources);          // the people did not
            Assert.Contains(f.InstructorId, resources);
        }
    }
}
