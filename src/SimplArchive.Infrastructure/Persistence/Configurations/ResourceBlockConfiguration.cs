using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.Booking;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.ServiceAccounts;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

public class ResourceBlockConfiguration : IEntityTypeConfiguration<ResourceBlock>
{
    public void Configure(EntityTypeBuilder<ResourceBlock> builder)
    {
        builder.HasKey(b => b.Id);

        // The scan both rules need: all Active blocks of one resource, ordered by start. It answers the
        // refusal on create ("is this slot blocked?") and the derived suspension ("which bookings does this
        // block catch?") — the same shape as the booking index, because they are the same question asked of
        // the other table.
        builder.HasIndex(b => new { b.TenantId, b.ResourceDocumentId, b.StartsAtUtc });

        // One block per document. Deliberately NOT the booking's widened (document, resource) pair: a block
        // withdraws ONE resource, so a second row for the same document would be a second grounding wearing
        // one justification — which is exactly what the booking's original rule said before multi-resource
        // bookings made it false there. There is no equivalent of a training flight here: grounding an
        // aircraft says nothing about anybody else.
        builder.HasIndex(b => new { b.TenantId, b.BlockDocumentId }).IsUnique();

        builder.ToTable(t => t.HasCheckConstraint(
            "CK_ResourceBlocks_WindowHasExtent",
            "\"StartsAtUtc\" < \"EndsAtUtc\""));

        builder.ToTable(t => t.HasCheckConstraint(
            "CK_ResourceBlocks_ExactlyOneCreator",
            "(CASE WHEN \"BlockedByUserId\" IS NOT NULL THEN 1 ELSE 0 END + " +
            "CASE WHEN \"BlockedByServiceAccountId\" IS NOT NULL THEN 1 ELSE 0 END) = 1"));

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(b => b.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        // Restrict, as for a booking: deleting a resource that is out of service should refuse rather than
        // silently drop the record of why it was.
        builder.HasOne<Document>()
            .WithMany()
            .HasForeignKey(b => b.ResourceDocumentId)
            .OnDelete(DeleteBehavior.Restrict);

        // BlockDocumentId is a PLAIN COLUMN, not a FK — the ResourceBooking.BookingDocumentId precedent
        // (ADR 0744 / 0503). A Cleared row is the durable answer to "when was this aircraft unavailable,
        // and why", and it has to outlive a purge of the .ics that created it.
        builder.HasIndex(b => b.BlockDocumentId);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(b => b.BlockedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ServiceAccount>()
            .WithMany()
            .HasForeignKey(b => b.BlockedByServiceAccountId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
