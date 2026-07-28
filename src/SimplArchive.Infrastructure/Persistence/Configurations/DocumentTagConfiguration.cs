using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Tenants;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

// See ADR "Document tags". One row per (document, tag); the unique index prevents a duplicate tag on a
// document, the (TenantId, Tag) index backs the distinct-tags autocomplete query. Cascade from the document.
public class DocumentTagConfiguration : IEntityTypeConfiguration<DocumentTag>
{
    public void Configure(EntityTypeBuilder<DocumentTag> builder)
    {
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Tag).IsRequired().HasMaxLength(100);

        builder.HasIndex(t => new { t.TenantId, t.DocumentId, t.Tag }).IsUnique();
        builder.HasIndex(t => new { t.TenantId, t.Tag });

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(t => t.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Document>()
            .WithMany()
            .HasForeignKey(t => t.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
