using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Booking;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// IsOfferedAsync (ABI 0.16, ADR 0782): the question a module asks when somebody's published availability is
// what stands in for their consent.
//
// The cases that matter are the ones a module re-deriving this would get wrong. COVERAGE, not overlap: a
// window that merely touches the slot does not consent to it. And coverage by a SINGLE window: two adjacent
// windows are not joined into one offer, because joining them infers an intent nobody stated. Both are
// decisions from ADR 0780 rather than arithmetic, which is why this lives behind the facade at all.
public class ModuleFacadeOfferedTimeTests
{
    private sealed class TestUserAccessor : ICurrentUserAccessor { public Guid? UserId { get; set; } }
    private sealed class TestServiceAccountAccessor : ICurrentServiceAccountAccessor { public Guid? ServiceAccountId { get; set; } }

    private static SimplArchiveDbContext Ctx(SqliteConnection c, Guid? tenantId = null) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(c).Options,
            new CurrentTenantAccessor { TenantId = tenantId });

    private static DateTimeOffset At(int hour) => new(2027, 6, 4, hour, 0, 0, TimeSpan.Zero);

    /// <summary>A tenant with one bookable resource and the given offered windows on it.</summary>
    private static async Task<(ModuleArchiveFacade Facade, Guid ResourceId)> RigAsync(
        SqliteConnection connection, params (int From, int To)[] windows)
    {
        using (var setup = Ctx(connection)) await setup.Database.EnsureCreatedAsync();

        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var resourceId = Guid.NewGuid();

        var maskId = Guid.NewGuid();
        var maskVersionId = Guid.NewGuid();

        var context = Ctx(connection, tenantId);
        context.Tenants.Add(new Tenant { Id = tenantId, Name = "T", CreatedAt = DateTimeOffset.UtcNow });
        context.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "p@x.io", DisplayName = "P", CreatedAt = DateTimeOffset.UtcNow });

        // The resource must wear a BOOKABLE mask or the availability invariant refuses the window outright
        // (ADR 0735) — offered time only means something for something that can be booked.
        context.Masks.Add(new Mask
        {
            Id = maskId,
            TenantId = tenantId,
            IsFolderMask = true,
            IsBookable = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        context.MaskVersions.Add(new MaskVersion
        {
            Id = maskVersionId,
            TenantId = tenantId,
            MaskId = maskId,
            Name = "Pilot dossier",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        context.Documents.Add(new Document
        {
            Id = resourceId,
            TenantId = tenantId,
            Name = "Instructor dossier",
            MaskVersionId = maskVersionId,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        // Saved BEFORE the windows: the availability invariant asks the database whether the resource's mask
        // is bookable, and a mask still sitting in the change tracker is a mask the query cannot see.
        await context.SaveChangesAsync();

        foreach (var (from, to) in windows)
        {
            context.ResourceAvailability.Add(new ResourceAvailability
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ResourceDocumentId = resourceId,
                WindowDocumentId = Guid.NewGuid(),
                StartsAtUtc = At(from),
                EndsAtUtc = At(to),
                Status = AvailabilityStatus.Offered,
                OfferedByUserId = userId,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        await context.SaveChangesAsync();

        var facade = new ModuleArchiveFacade(
            context, new TestUserAccessor { UserId = userId }, new TestServiceAccountAccessor());
        return (facade, resourceId);
    }

    [Theory]
    // Inside the offer, and exactly the offer — both consent.
    [InlineData(10, 11, true)]
    [InlineData(9, 12, true)]
    // Touching is NOT covering: a window ending at 12:00 does not consent to a flight running to 13:00.
    // This is the mistake a module testing overlap would make, and it would be invisible.
    [InlineData(11, 13, false)]
    [InlineData(8, 10, false)]
    // Straddling the offer entirely.
    [InlineData(8, 13, false)]
    // Adjacent to it, sharing only an instant.
    [InlineData(12, 13, false)]
    public async Task Coverage_not_overlap_is_what_consents(int from, int to, bool expected)
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var (facade, resourceId) = await RigAsync(connection, (9, 12));

        Assert.Equal(expected, await facade.IsOfferedAsync(resourceId, At(from), At(to)));
    }

    [Fact]
    public async Task Two_adjacent_windows_are_not_one_offer()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var (facade, resourceId) = await RigAsync(connection, (9, 12), (12, 15));

        // 10:00–14:00 lies inside the union and inside NEITHER window. Joining them would infer that somebody
        // offering two blocks meant to offer the span across them, which ADR 0780 declines to assume — a
        // resource offering continuous time publishes it as one window.
        Assert.False(await facade.IsOfferedAsync(resourceId, At(10), At(14)));

        // Each window still consents to what it actually covers.
        Assert.True(await facade.IsOfferedAsync(resourceId, At(10), At(11)));
        Assert.True(await facade.IsOfferedAsync(resourceId, At(13), At(14)));
    }

    [Fact]
    public async Task A_withdrawn_window_does_not_consent()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var (facade, resourceId) = await RigAsync(connection, (9, 12));

        await using (var context = Ctx(connection))
        {
            var window = await context.ResourceAvailability.IgnoreQueryFilters().SingleAsync();
            window.Status = AvailabilityStatus.Withdrawn;
            await context.SaveChangesAsync();
        }

        // The row stays as its own history (ADR 0780) — but history is not an offer.
        Assert.False(await facade.IsOfferedAsync(resourceId, At(10), At(11)));
    }

    [Fact]
    public async Task A_resource_nobody_offered_time_for_answers_false()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var (facade, resourceId) = await RigAsync(connection);

        // The safe answer and the honest one: absence of an offer is not consent.
        Assert.False(await facade.IsOfferedAsync(resourceId, At(10), At(11)));
    }

    [Fact]
    public async Task An_unknown_document_answers_false_rather_than_throwing()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var (facade, _) = await RigAsync(connection, (9, 12));

        // The tenant is taken from the RESOURCE, so an id from elsewhere resolves to no tenant and therefore
        // to no offer — never to another tenant's windows.
        Assert.False(await facade.IsOfferedAsync(Guid.NewGuid(), At(10), At(11)));
    }

    /// <summary>Books the resource for a slot, so "free" has something to say no to.</summary>
    private static async Task ClaimAsync(SqliteConnection connection, Guid resourceId, int from, int to)
    {
        await using var context = Ctx(connection);
        var resource = await context.Documents.IgnoreQueryFilters().SingleAsync(d => d.Id == resourceId);
        context.ResourceBookings.Add(new ResourceBooking
        {
            Id = Guid.NewGuid(),
            TenantId = resource.TenantId,
            ResourceDocumentId = resourceId,
            BookingDocumentId = Guid.NewGuid(),
            StartsAtUtc = At(from),
            EndsAtUtc = At(to),
            Status = BookingStatus.Active,
            BookedByUserId = resource.CreatedByUserId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync();
    }

    [Theory]
    // Overlapping a claim in any way makes the resource busy.
    [InlineData(10, 11, false)]
    [InlineData(9, 13, false)]
    [InlineData(11, 14, false)]
    // TOUCHING is not overlapping: a booking ending at 12:00 leaves 12:00 onwards free. The invariant uses
    // half-open [start, end) and this must agree with it — which is why both now read the same helper.
    [InlineData(12, 14, true)]
    [InlineData(7, 10, true)]
    public async Task Free_means_nothing_else_claims_the_slot(int from, int to, bool expected)
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var (facade, resourceId) = await RigAsync(connection);
        await ClaimAsync(connection, resourceId, 10, 12);

        Assert.Equal(expected, await facade.IsFreeAsync(resourceId, At(from), At(to)));
    }

    [Fact]
    public async Task A_resource_nobody_has_booked_is_free()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var (facade, resourceId) = await RigAsync(connection);

        Assert.True(await facade.IsFreeAsync(resourceId, At(10), At(11)));
    }

    [Fact]
    public async Task A_cancelled_claim_does_not_make_a_resource_busy()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var (facade, resourceId) = await RigAsync(connection);
        await ClaimAsync(connection, resourceId, 10, 12);

        await using (var context = Ctx(connection))
        {
            var claim = await context.ResourceBookings.IgnoreQueryFilters().SingleAsync();
            claim.Status = BookingStatus.Cancelled;
            await context.SaveChangesAsync();
        }

        // A cancelled claim is history, not a commitment — the same reading the invariant takes.
        Assert.True(await facade.IsFreeAsync(resourceId, At(10), At(11)));
    }

    [Fact]
    public async Task Offered_and_free_are_different_questions()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var (facade, resourceId) = await RigAsync(connection, (9, 13));
        await ClaimAsync(connection, resourceId, 10, 12);

        // This is the whole reason the second question exists: somebody who published the afternoon and was
        // then booked is STILL OFFERING it and can no longer take it. A picker built on the offer alone
        // lists a name the booking will refuse.
        Assert.True(await facade.IsOfferedAsync(resourceId, At(10), At(11)));
        Assert.False(await facade.IsFreeAsync(resourceId, At(10), At(11)));
    }
}
