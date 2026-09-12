using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Booking;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// A resource that has published availability means it (#1124).
//
// Reported from use: a window was published for a room, an appointment was made that STARTED inside it and
// ENDED outside, and it was accepted. Core used to leave the question entirely to an industry module's
// booking rule — so a plain installation had an Availability collection that did nothing at all, and a window
// nobody honoured.
//
// The gate is "has this resource published at least one window": one that has published none books exactly as
// before, so the rule cannot surprise anyone who never used availability. Enforced in SaveChanges beside the
// block and overlap invariants, which is the one door every write path goes through — the app, CalDAV and
// WebDAV alike.
public class AvailabilityEnforcementTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, Guid? tenantId = null) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options,
            new CurrentTenantAccessor { TenantId = tenantId });

    private sealed record Fixture(
        Guid TenantId, Guid UserId, Guid RoomId, Guid BookingDocumentId, Guid WindowDocumentId, Guid SecondWindowDocumentId);

    private static async Task<Fixture> SeedAsync(SqliteConnection connection)
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var maskId = Guid.NewGuid();
        var maskVersionId = Guid.NewGuid();
        var roomId = Guid.NewGuid();
        var bookingDocId = Guid.NewGuid();
        var windowDocId = Guid.NewGuid();
        // A window IS its .ics document, and one document may name only one window (a unique index on
        // TenantId+WindowDocumentId) — so a second window needs a second document, not a second row.
        var secondWindowDocId = Guid.NewGuid();

        using var seed = CreateContext(connection);
        seed.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
        seed.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "a@example.com", DisplayName = "A", CreatedAt = DateTimeOffset.UtcNow });
        seed.Masks.Add(new Mask { Id = maskId, TenantId = tenantId, IsBookable = true, CreatedAt = DateTimeOffset.UtcNow });
        seed.MaskVersions.Add(new MaskVersion { Id = maskVersionId, TenantId = tenantId, MaskId = maskId, Name = "Meeting room", CreatedAt = DateTimeOffset.UtcNow });
        seed.Documents.Add(new Document { Id = roomId, TenantId = tenantId, Name = "Fine dining room", MaskVersionId = maskVersionId, CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
        seed.Documents.Add(new Document { Id = bookingDocId, TenantId = tenantId, Name = "Dinner", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
        seed.Documents.Add(new Document { Id = windowDocId, TenantId = tenantId, Name = "Open hours", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
        seed.Documents.Add(new Document { Id = secondWindowDocId, TenantId = tenantId, Name = "Late hours", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
        await seed.SaveChangesAsync();

        return new Fixture(tenantId, userId, roomId, bookingDocId, windowDocId, secondWindowDocId);
    }

    private static DateTimeOffset At(int hour) => new(2026, 9, 10, hour, 0, 0, TimeSpan.Zero);

    private static ResourceAvailability Window(
        Fixture f, int startHour, int endHour, AvailabilityStatus status = AvailabilityStatus.Offered, Guid? windowDocumentId = null) => new()
        {
            Id = Guid.NewGuid(),
            TenantId = f.TenantId,
            ResourceDocumentId = f.RoomId,
            WindowDocumentId = windowDocumentId ?? f.WindowDocumentId,
            StartsAtUtc = At(startHour),
            EndsAtUtc = At(endHour),
            Status = status,
            // Exactly one of OfferedBy{User,ServiceAccount}Id is set — a CHECK constraint, so an unset pair fails
            // the save rather than the invariant under test.
            OfferedByUserId = f.UserId,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    private static ResourceBooking Booking(Fixture f, int startHour, int endHour) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = f.TenantId,
        ResourceDocumentId = f.RoomId,
        BookingDocumentId = f.BookingDocumentId,
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

    private static async Task<BookingInvariantException> RefusedAsync(SqliteConnection connection, Fixture f, int startHour, int endHour)
    {
        using var context = CreateContext(connection, f.TenantId);
        context.ResourceBookings.Add(Booking(f, startHour, endHour));
        return await Assert.ThrowsAsync<BookingInvariantException>(() => context.SaveChangesAsync());
    }

    private static async Task AcceptedAsync(SqliteConnection connection, Fixture f, int startHour, int endHour)
    {
        using var context = CreateContext(connection, f.TenantId);
        context.ResourceBookings.Add(Booking(f, startHour, endHour));
        await context.SaveChangesAsync();
    }

    private static async Task PublishAsync(SqliteConnection connection, Fixture f, ResourceAvailability window)
    {
        using var context = CreateContext(connection, f.TenantId);
        context.ResourceAvailability.Add(window);
        await context.SaveChangesAsync();
    }

    // The reported case, exactly: 10:00–12:00 offered, 11:00–13:00 requested. Half an offer must not authorise
    // a whole booking — which is why coverage, not overlap, is the test.
    [Fact]
    public async Task A_booking_that_ends_outside_the_window_is_refused()
    {
        var (connection, f) = await StartAsync();
        using var _ = connection;
        await PublishAsync(connection, f, Window(f, 10, 12));

        var error = await RefusedAsync(connection, f, 11, 13);

        Assert.Equal(BookingInvariantKind.NotOffered, error.Kind);
    }

    [Fact]
    public async Task A_booking_that_starts_before_the_window_is_refused()
    {
        var (connection, f) = await StartAsync();
        using var _ = connection;
        await PublishAsync(connection, f, Window(f, 10, 12));

        Assert.Equal(BookingInvariantKind.NotOffered, (await RefusedAsync(connection, f, 9, 11)).Kind);
    }

    [Fact]
    public async Task A_booking_inside_the_window_is_accepted()
    {
        var (connection, f) = await StartAsync();
        using var _ = connection;
        await PublishAsync(connection, f, Window(f, 10, 12));

        await AcceptedAsync(connection, f, 10, 11);
    }

    // The whole window is coverage too — a slot equal to the offer is covered by it.
    [Fact]
    public async Task A_booking_filling_the_window_exactly_is_accepted()
    {
        var (connection, f) = await StartAsync();
        using var _ = connection;
        await PublishAsync(connection, f, Window(f, 10, 12));

        await AcceptedAsync(connection, f, 10, 12);
    }

    // THE GATE, and the reason this rule can ship without a setting: a resource that never published anything
    // books exactly as it did before, so nobody is surprised by a rule they did not opt into.
    [Fact]
    public async Task A_resource_that_has_published_nothing_books_freely()
    {
        var (connection, f) = await StartAsync();
        using var _ = connection;

        await AcceptedAsync(connection, f, 3, 4);
    }

    // A withdrawn window is not an offer — so withdrawing the only window returns the resource to the
    // un-published state rather than locking it out of every hour.
    [Fact]
    public async Task A_withdrawn_window_does_not_gate_anything()
    {
        var (connection, f) = await StartAsync();
        using var _ = connection;
        await PublishAsync(connection, f, Window(f, 10, 12, AvailabilityStatus.Withdrawn));

        await AcceptedAsync(connection, f, 3, 4);
    }

    // Coverage by a SINGLE window, deliberately (ADR 0780): two adjacent windows are not merged into one
    // offer, because treating them as one is an inference about somebody's intent the core should not make.
    // Pinned so a later "improvement" has to argue with the decision rather than discover it.
    [Fact]
    public async Task Two_adjacent_windows_do_not_add_up_to_one_offer()
    {
        var (connection, f) = await StartAsync();
        using var _ = connection;
        await PublishAsync(connection, f, Window(f, 10, 12));
        await PublishAsync(connection, f, Window(f, 12, 14, windowDocumentId: f.SecondWindowDocumentId));

        Assert.Equal(BookingInvariantKind.NotOffered, (await RefusedAsync(connection, f, 11, 13)).Kind);
    }
}
