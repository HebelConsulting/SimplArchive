using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.PlatformAdministrators;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

public class PlatformAdministratorConfiguration : IEntityTypeConfiguration<PlatformAdministrator>
{
    public void Configure(EntityTypeBuilder<PlatformAdministrator> builder)
    {
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Name).IsRequired().HasMaxLength(200);
        builder.Property(p => p.OpenIddictApplicationClientId).IsRequired();

        // Global uniqueness, not per-tenant — PlatformAdministrator isn't ITenantScoped, so there's no
        // tenant to scope either index by (unlike ServiceAccount's (TenantId, Name)/(TenantId, ClientId)).
        builder.HasIndex(p => p.Name).IsUnique();
        builder.HasIndex(p => p.OpenIddictApplicationClientId).IsUnique();
    }
}
