using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

// See ADR "Document subscriptions". One subscription per (user, document), enforced by the unique index.
// Cascade from both the user and the document, so a subscription is removed when either goes away.
public class DocumentSubscriptionConfiguration : IEntityTypeConfiguration<DocumentSubscription>
{
    public void Configure(EntityTypeBuilder<DocumentSubscription> builder)
    {
        builder.HasKey(s => s.Id);

        builder.HasIndex(s => new { s.TenantId, s.UserId, s.DocumentId }).IsUnique();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(s => s.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Document>()
            .WithMany()
            .HasForeignKey(s => s.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
