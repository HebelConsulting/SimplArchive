using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.Booking;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.ServiceAccounts;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

public class ResourceAvailabilityConfiguration : IEntityTypeConfiguration<ResourceAvailability>
{
    public void Configure(EntityTypeBuilder<ResourceAvailability> builder)
    {
        builder.HasKey(a => a.Id);

        // "Which windows does this resource offer around then?" — the one scan both the module's booking
        // rule and the derived committed label need. Same shape as the booking and block indexes, because it
        // is the same question asked of the third table.
        builder.HasIndex(a => new { a.TenantId, a.ResourceDocumentId, a.StartsAtUtc });

        // One window per document, like a block and unlike a booking: a window offers ONE resource's time,
        // so a second row for the same document would be a second offer wearing one justification. (A
        // booking's rule is widened because a training flight is one justification for three claims — there
        // is no equivalent here.)
        builder.HasIndex(a => new { a.TenantId, a.WindowDocumentId }).IsUnique();

        builder.ToTable(t => t.HasCheckConstraint(
            "CK_ResourceAvailability_WindowHasExtent",
            "\"StartsAtUtc\" < \"EndsAtUtc\""));

        builder.ToTable(t => t.HasCheckConstraint(
            "CK_ResourceAvailability_ExactlyOneOfferer",
            "(CASE WHEN \"OfferedByUserId\" IS NOT NULL THEN 1 ELSE 0 END + " +
            "CASE WHEN \"OfferedByServiceAccountId\" IS NOT NULL THEN 1 ELSE 0 END) = 1"));

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(a => a.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Document>()
            .WithMany()
            .HasForeignKey(a => a.ResourceDocumentId)
            .OnDelete(DeleteBehavior.Restrict);

        // A plain column, not a FK — the ResourceBooking/ResourceBlock precedent (ADRs 0744/0778/0503): a
        // Withdrawn row is the durable answer to "was this time offered when the flight was booked?", and
        // must outlive a purge of the .ics that created it.
        builder.HasIndex(a => a.WindowDocumentId);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(a => a.OfferedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ServiceAccount>()
            .WithMany()
            .HasForeignKey(a => a.OfferedByServiceAccountId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
