using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.Booking;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.ServiceAccounts;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

public class ResourceBookingConfiguration : IEntityTypeConfiguration<ResourceBooking>
{
    public void Configure(EntityTypeBuilder<ResourceBooking> builder)
    {
        builder.HasKey(b => b.Id);

        // The overlap invariant's scan: all Active bookings of one resource, ordered by start. The
        // no-overlap rule itself cannot be a unique index (ranges don't unique portably — a Postgres
        // exclusion constraint has no SQLite equivalent, ADR 0735), so it lives in SaveChanges.
        builder.HasIndex(b => new { b.TenantId, b.ResourceDocumentId, b.StartsAtUtc });

        // One claim per (document, RESOURCE) — widened from the original "one row per booking document"
        // (ADR 0774). That rule read "two rows would be two claims wearing one justification", and it held
        // for as long as a booking meant one resource. A training flight is one justification for three
        // claims: the same window occupies the aircraft, the student and the instructor, and there is no
        // second document to write. What stays forbidden is the same resource claimed twice by one booking,
        // which is a duplicate rather than a second participant.
        builder.HasIndex(b => new { b.TenantId, b.BookingDocumentId, b.ResourceDocumentId }).IsUnique();

        // A slot must have extent: zero-length or inverted ranges would vacuously never overlap anything.
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_ResourceBookings_SlotHasExtent",
            "\"StartsAtUtc\" < \"EndsAtUtc\""));

        // Exactly one of BookedByUserId/BookedByServiceAccountId — the DocumentReference precedent.
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_ResourceBookings_ExactlyOneBooker",
            "(CASE WHEN \"BookedByUserId\" IS NOT NULL THEN 1 ELSE 0 END + " +
            "CASE WHEN \"BookedByServiceAccountId\" IS NOT NULL THEN 1 ELSE 0 END) = 1"));

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(b => b.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        // Deleting a resource with bookings is refused at the database as the last line (ADR 0735: cancel
        // bookings first); the API layer refuses it earlier with a specific error.
        builder.HasOne<Document>()
            .WithMany()
            .HasForeignKey(b => b.ResourceDocumentId)
            .OnDelete(DeleteBehavior.Restrict);

        // BookingDocumentId is deliberately a PLAIN COLUMN, not a FK (ADR 0744) — the
        // Document.CurrentVersionId precedent (ADR 0503). The booking document is the .ics itself now, and
        // the row is the durable history: a purge of the document must leave the Cancelled row standing,
        // which a cascade would erase. Deleting the document Cancels the row in SaveChanges instead.
        builder.HasIndex(b => b.BookingDocumentId);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(b => b.BookedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ServiceAccount>()
            .WithMany()
            .HasForeignKey(b => b.BookedByServiceAccountId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
