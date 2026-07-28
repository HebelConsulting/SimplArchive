using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.Acl;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Groups;
using SimplArchive.Domain.ServiceAccounts;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

public class AclEntryConfiguration : IEntityTypeConfiguration<AclEntry>
{
    public void Configure(EntityTypeBuilder<AclEntry> builder)
    {
        builder.HasKey(a => a.Id);

        // Exactly one of UserId/GroupId/ServiceAccountId is set — see ADR "ACL entry data shape
        // (repository-scoped slice)", ADR "AclEntry ServiceAccount principal (schema-only slice)".
        // Expressed via CASE WHEN rather than a chain of IS NULL/IS NOT NULL comparisons so it reads as
        // "exactly one" directly, and stays portable standard SQL across PostgreSQL/SQLite.
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_AclEntries_ExactlyOnePrincipal",
            "(CASE WHEN \"UserId\" IS NOT NULL THEN 1 ELSE 0 END + " +
            "CASE WHEN \"GroupId\" IS NOT NULL THEN 1 ELSE 0 END + " +
            "CASE WHEN \"ServiceAccountId\" IS NOT NULL THEN 1 ELSE 0 END) = 1"));

        // At least one right must be granted — see ADR "Reject empty AclEntry grants". An all-false row
        // is a no-op grant with no functional purpose, almost certainly an admin/UI error.
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_AclEntries_AtLeastOneRight",
            "\"CanSee\" OR \"CanReadContent\" OR \"CanEditContent\" OR \"CanEditIndexData\" OR \"CanDelete\" OR \"CanCreateSubItems\" OR \"CanManagePermissions\" OR \"CanMove\" OR \"CanAnnotate\""));

        // Three partial indexes, not six — AclEntry is Document-scoped only now (ADR
        // "Repository/Document unification"; DocumentId is required, no more Repository/Document XOR).
        // A "repository-level" grant is just a grant on a root Document (ParentId == null).
        builder.HasIndex(a => new { a.DocumentId, a.UserId })
            .IsUnique()
            .HasFilter("\"UserId\" IS NOT NULL");

        builder.HasIndex(a => new { a.DocumentId, a.GroupId })
            .IsUnique()
            .HasFilter("\"GroupId\" IS NOT NULL");

        builder.HasIndex(a => new { a.DocumentId, a.ServiceAccountId })
            .IsUnique()
            .HasFilter("\"ServiceAccountId\" IS NOT NULL");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(a => a.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Document>()
            .WithMany()
            .HasForeignKey(a => a.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(a => a.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Group>()
            .WithMany()
            .HasForeignKey(a => a.GroupId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<ServiceAccount>()
            .WithMany()
            .HasForeignKey(a => a.ServiceAccountId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
