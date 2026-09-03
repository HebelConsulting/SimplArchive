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

        // One booking row per booking document — the document IS the booking's payload; two rows would be
        // two claims wearing one justification.
        builder.HasIndex(b => new { b.TenantId, b.BookingDocumentId }).IsUnique();

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

        // A hard-deleted booking document takes its claim with it (normal deletes are soft and translate
        // to Status = Cancelled in the delete path, not here).
        builder.HasOne<Document>()
            .WithMany()
            .HasForeignKey(b => b.BookingDocumentId)
            .OnDelete(DeleteBehavior.Cascade);

        // AppointmentDocumentId is deliberately a PLAIN COLUMN, not a FK — the Document.CurrentVersionId
        // precedent (ADR 0503): a FK would entangle the appointment's delete cascade with the booking.

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
