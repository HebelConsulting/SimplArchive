using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.Groups;
using SimplArchive.Domain.Search;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

// See ADR "Scoped saved-search sharing". A grant of a saved search to exactly one principal (user or group).
// Cascade from the saved search (deleting a search removes its shares) and from the principal.
public class SavedSearchShareConfiguration : IEntityTypeConfiguration<SavedSearchShare>
{
    public void Configure(EntityTypeBuilder<SavedSearchShare> builder)
    {
        builder.HasKey(s => s.Id);

        // No duplicate grant to the same principal — one partial unique index per principal type (the same
        // NULL != NULL reasoning as AclEntry's three partial indexes).
        builder.HasIndex(s => new { s.TenantId, s.SavedSearchId, s.UserId })
            .IsUnique()
            .HasFilter("\"UserId\" IS NOT NULL");
        builder.HasIndex(s => new { s.TenantId, s.SavedSearchId, s.GroupId })
            .IsUnique()
            .HasFilter("\"GroupId\" IS NOT NULL");

        // Exactly one of UserId/GroupId is set — same CASE WHEN "exactly one" shape as AclEntry's principal.
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_SavedSearchShares_ExactlyOnePrincipal",
            "(CASE WHEN \"UserId\" IS NOT NULL THEN 1 ELSE 0 END + " +
            "CASE WHEN \"GroupId\" IS NOT NULL THEN 1 ELSE 0 END) = 1"));

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(s => s.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<SavedSearch>()
            .WithMany()
            .HasForeignKey(s => s.SavedSearchId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Group>()
            .WithMany()
            .HasForeignKey(s => s.GroupId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
