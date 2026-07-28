using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.Groups;
using SimplArchive.Domain.Tenants;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

public class GroupConfiguration : IEntityTypeConfiguration<Group>
{
    public void Configure(EntityTypeBuilder<Group> builder)
    {
        builder.HasKey(g => g.Id);
        builder.Property(g => g.Name).IsRequired().HasMaxLength(200);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(g => g.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Group>()
            .WithMany()
            .HasForeignKey(g => g.ParentGroupId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
