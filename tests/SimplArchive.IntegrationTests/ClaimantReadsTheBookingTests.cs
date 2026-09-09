using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Acl;
using SimplArchive.Domain.Booking;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Acl;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// A claimant reads the booking they are part of (ADR 0775): a student booked onto a training flight can read
// the flight's .ics, which lives in the AIRCRAFT's Schedule where they hold no grant at all.
//
// This is a floor over the ACL, so the tests that matter are the ones about its EDGES: it must not lift a
// clearance bar, must not widen a grant somebody deliberately made narrow, must not survive a cancellation,
// and must not leak anything beyond the one document the claim points at.
public class ClaimantReadsTheBookingTests
{
    private static SimplArchiveDbContext Ctx(SqliteConnection connection, Guid? tenantId = null) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options,
            new CurrentTenantAccessor { TenantId = tenantId });

    private sealed record Fixture(Guid TenantId, Guid UserId, Guid DossierId, Guid AircraftId, Guid ScheduleId, Guid FlightId);

    /// <summary>A pilot with a dossier that REPRESENTS them, an aircraft holding the flight's .ics, and no ACL
    /// grant anywhere for the pilot — so every right the pilot ends up with came from the claim.</summary>
    private static async Task<Fixture> SeedAsync(SqliteConnection connection, bool withClaim = true, BookingStatus status = BookingStatus.Active)
    {
        var f = new Fixture(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var maskId = Guid.NewGuid();
        var maskVersionId = Guid.NewGuid();

        using var seed = Ctx(connection);
        seed.Tenants.Add(new Tenant { Id = f.TenantId, Name = $"School {f.TenantId}", CreatedAt = DateTimeOffset.UtcNow });
        seed.Users.Add(new User { Id = f.UserId, TenantId = f.TenantId, Email = $"{f.UserId}@example.com", DisplayName = "Pilot", CreatedAt = DateTimeOffset.UtcNow });
        seed.Masks.Add(new Mask { Id = maskId, TenantId = f.TenantId, IsBookable = true, CreatedAt = DateTimeOffset.UtcNow });
        seed.MaskVersions.Add(new MaskVersion { Id = maskVersionId, TenantId = f.TenantId, MaskId = maskId, Name = "Bookable", CreatedAt = DateTimeOffset.UtcNow });

        void Doc(Guid id, Guid? parent, string name, Guid? version = null) =>
            seed.Documents.Add(new Document
            {
                Id = id,
                TenantId = f.TenantId,
                ParentId = parent,
                Name = name,
                MaskVersionId = version,
                CreatedByUserId = f.UserId,
                CreatedAt = DateTimeOffset.UtcNow,
            });

        Doc(f.DossierId, null, "Pilot dossier", maskVersionId);
        Doc(f.AircraftId, null, "HB-PHG", maskVersionId);
        Doc(f.ScheduleId, f.AircraftId, "Schedule");
        Doc(f.FlightId, f.ScheduleId, "Flight");
        await seed.SaveChangesAsync();

        seed.ResourcePrincipals.Add(new ResourcePrincipal
        {
            Id = Guid.NewGuid(),
            TenantId = f.TenantId,
            ResourceDocumentId = f.DossierId,
            UserId = f.UserId,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        if (withClaim)
        {
            seed.ResourceBookings.Add(new ResourceBooking
            {
                Id = Guid.NewGuid(),
                TenantId = f.TenantId,
                ResourceDocumentId = f.DossierId,
                BookingDocumentId = f.FlightId,
                StartsAtUtc = new DateTimeOffset(2026, 9, 10, 10, 0, 0, TimeSpan.Zero),
                EndsAtUtc = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero),
                Status = status,
                BookedByUserId = f.UserId,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        await seed.SaveChangesAsync();
        return f;
    }

    private static async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using var setup = Ctx(connection);
        await setup.Database.EnsureCreatedAsync();
        return connection;
    }

    [Fact]
    public async Task A_claimant_may_see_and_read_the_booking_document()
    {
        using var connection = await OpenAsync();
        var f = await SeedAsync(connection);

        using var context = Ctx(connection, f.TenantId);
        var rights = await new EffectiveRightsCalculator(context).GetEffectiveRightsAsync(f.UserId, f.FlightId);

        Assert.True(rights.CanSee);
        Assert.True(rights.CanReadContent);

        // A floor, not a promotion: being on the flight does not let anyone edit or delete it.
        Assert.False(rights.CanEditContent);
        Assert.False(rights.CanDelete);
    }

    // The control. Same fixture, same absence of grants, no claim — so the previous test cannot be passing
    // because the calculator is simply generous.
    [Fact]
    public async Task Without_a_claim_the_same_document_stays_invisible()
    {
        using var connection = await OpenAsync();
        var f = await SeedAsync(connection, withClaim: false);

        using var context = Ctx(connection, f.TenantId);
        var rights = await new EffectiveRightsCalculator(context).GetEffectiveRightsAsync(f.UserId, f.FlightId);

        Assert.False(rights.CanSee);
        Assert.False(rights.CanReadContent);
    }

    // A cancelled claim is history. The flight is off, and a withdrawn commitment should not keep buying read
    // access to a document the ACL otherwise withholds.
    [Fact]
    public async Task A_cancelled_claim_grants_nothing()
    {
        using var connection = await OpenAsync();
        var f = await SeedAsync(connection, status: BookingStatus.Cancelled);

        using var context = Ctx(connection, f.TenantId);
        var rights = await new EffectiveRightsCalculator(context).GetEffectiveRightsAsync(f.UserId, f.FlightId);

        Assert.False(rights.CanSee);
    }

    // The mapping is what makes a claim a PERSON's. Without it the claim names a document and nobody, which
    // is the state every resource is in until a module declares otherwise — and it must grant nothing.
    [Fact]
    public async Task A_claim_on_a_resource_that_represents_nobody_grants_nothing()
    {
        using var connection = await OpenAsync();
        var f = await SeedAsync(connection);

        using (var context = Ctx(connection, f.TenantId))
        {
            context.ResourcePrincipals.RemoveRange(await context.ResourcePrincipals.ToListAsync());
            await context.SaveChangesAsync();
        }

        using var check = Ctx(connection, f.TenantId);
        var rights = await new EffectiveRightsCalculator(check).GetEffectiveRightsAsync(f.UserId, f.FlightId);

        Assert.False(rights.CanSee);
    }

    // Scoped to the one document the claim points at. The Schedule that holds the flight, and the aircraft
    // above it, stay exactly as invisible as they were — being on a flight is not being given the fleet.
    [Fact]
    public async Task The_grant_reaches_the_booking_document_and_nothing_around_it()
    {
        using var connection = await OpenAsync();
        var f = await SeedAsync(connection);

        using var context = Ctx(connection, f.TenantId);
        var calculator = new EffectiveRightsCalculator(context);

        Assert.True((await calculator.GetEffectiveRightsAsync(f.UserId, f.FlightId)).CanSee);
        Assert.False((await calculator.GetEffectiveRightsAsync(f.UserId, f.ScheduleId)).CanSee);
        Assert.False((await calculator.GetEffectiveRightsAsync(f.UserId, f.AircraftId)).CanSee);
    }

    // A grant somebody made deliberately narrow is not widened. The floor fires only where CanSee is absent —
    // the same rule the access-without-grant right already follows, and for the same reason: a real grant is
    // a decision, and silently topping it up would make grants unreadable.
    //
    // The grant goes on the AIRCRAFT, not on the flight. Rights resolve at the GOVERNING SCOPE (ADR 0183), so
    // an entry on a document that does not break inheritance is inert — written on the flight first, this test
    // failed with CanReadContent true, and the reason was not the floor misfiring but the grant never existing.
    [Fact]
    public async Task A_narrow_existing_grant_is_left_alone()
    {
        using var connection = await OpenAsync();
        var f = await SeedAsync(connection);

        using (var context = Ctx(connection, f.TenantId))
        {
            context.AclEntries.Add(new AclEntry
            {
                Id = Guid.NewGuid(),
                TenantId = f.TenantId,
                DocumentId = f.AircraftId,
                UserId = f.UserId,
                CanSee = true,
                CanReadContent = false,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await context.SaveChangesAsync();
        }

        using var check = Ctx(connection, f.TenantId);
        var rights = await new EffectiveRightsCalculator(check).GetEffectiveRightsAsync(f.UserId, f.FlightId);

        Assert.True(rights.CanSee);
        Assert.False(rights.CanReadContent);   // NOT topped up by the claim
    }

    // The batch path is the one every listing uses, and the single-document path delegates to it — so a floor
    // that worked only in one of them would be a difference nobody sees until a page renders differently from
    // the document it opens.
    [Fact]
    public async Task The_batch_path_agrees_with_the_single_one()
    {
        using var connection = await OpenAsync();
        var f = await SeedAsync(connection);

        using var context = Ctx(connection, f.TenantId);
        var many = await new EffectiveRightsCalculator(context)
            .GetEffectiveRightsForManyAsync(f.UserId, [f.FlightId, f.ScheduleId]);

        Assert.True(many[f.FlightId].CanSee);
        Assert.False(many[f.ScheduleId].CanSee);
    }
}
