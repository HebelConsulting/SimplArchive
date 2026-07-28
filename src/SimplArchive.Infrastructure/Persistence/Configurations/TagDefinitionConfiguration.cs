using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Tenants;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

// See ADR "Tag controlled vocabulary". The admin-managed tag catalog; the unique (TenantId, Name) index keeps one
// catalog entry per normalized tag name per tenant.
public class TagDefinitionConfiguration : IEntityTypeConfiguration<TagDefinition>
{
    public void Configure(EntityTypeBuilder<TagDefinition> builder)
    {
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Name).IsRequired().HasMaxLength(100);
        builder.Property(t => t.Color).HasMaxLength(7);

        builder.HasIndex(t => new { t.TenantId, t.Name }).IsUnique();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(t => t.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
