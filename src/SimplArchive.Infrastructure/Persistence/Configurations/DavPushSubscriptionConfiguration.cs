using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.CalDav;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

// See ADR 0622. Unique on (FolderId, Endpoint) so a re-registration UPDATES rather than duplicating — clients
// re-register routinely, and a duplicate would mean sending the same notification twice. Cascade from both the
// folder and the user: a purged collection or a deleted account takes its subscriptions along.
public class DavPushSubscriptionConfiguration : IEntityTypeConfiguration<DavPushSubscription>
{
    public void Configure(EntityTypeBuilder<DavPushSubscription> builder)
    {
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Endpoint).IsRequired().HasMaxLength(2048);
        builder.Property(s => s.P256dh).IsRequired().HasMaxLength(256);
        builder.Property(s => s.Auth).IsRequired().HasMaxLength(256);

        builder.HasIndex(s => new { s.FolderId, s.Endpoint }).IsUnique();

        builder.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Document>().WithMany().HasForeignKey(s => s.FolderId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
