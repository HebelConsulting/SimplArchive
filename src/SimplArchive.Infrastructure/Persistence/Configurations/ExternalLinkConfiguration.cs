using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

public class ExternalLinkConfiguration : IEntityTypeConfiguration<ExternalLink>
{
    public void Configure(EntityTypeBuilder<ExternalLink> builder)
    {
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Token).IsRequired();

        // UNIQUE, and the lookup path for every anonymous request — the one query in the system that runs with no
        // principal at all, so it must resolve in one indexed hit (ADR 0546).
        //
        // Deliberately NOT scoped by tenant: the token is resolved BEFORE the tenant is known (it is what
        // identifies the tenant), so uniqueness has to hold across the whole table, not per tenant.
        builder.HasIndex(l => l.Token).IsUnique();

        // A document's own links, newest expiry last.
        builder.HasIndex(l => new { l.TenantId, l.DocumentId, l.ExpiresAt });

        // "Everything this person has shared" — the cross-document dialog, and the tenant admin's view of another
        // user's links (ADR 0546).
        builder.HasIndex(l => new { l.TenantId, l.CreatedByUserId, l.ExpiresAt });

        // No "exactly one creator" constraint here, unlike every other creator pair: only a User can create a
        // link (ADR 0546), so a required CreatedByUserId says the same thing with a NOT NULL instead of a CHECK.
        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(l => l.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        // Deleting a document kills its links — a share cannot outlive the thing it shares.
        builder.HasOne<Document>()
            .WithMany()
            .HasForeignKey(l => l.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(l => l.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
