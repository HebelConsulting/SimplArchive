using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

public class FieldValueConfiguration : IEntityTypeConfiguration<FieldValue>
{
    public void Configure(EntityTypeBuilder<FieldValue> builder)
    {
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Value).IsRequired();

        // Lookup index for "all field values on this document" and "all values of this field" reads.
        builder.HasIndex(f => new { f.DocumentId, f.FieldDefinitionId });

        // The per-field Unique constraint (ADR "Metadata field validation rules") was removed entirely —
        // see ADR "Repository/Document unification" — so there's no uniqueness index to maintain here.

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(f => f.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Document>()
            .WithMany()
            .HasForeignKey(f => f.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<FieldDefinition>()
            .WithMany()
            .HasForeignKey(f => f.FieldDefinitionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
