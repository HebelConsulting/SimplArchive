using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.Modules;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

public class ModuleSettingValueConfiguration : IEntityTypeConfiguration<ModuleSettingValue>
{
    public void Configure(EntityTypeBuilder<ModuleSettingValue> builder)
    {
        builder.HasKey(v => v.Id);

        builder.Property(v => v.ModuleId)
            .IsRequired()
            .HasMaxLength(128);   // as ModuleActivation.ModuleId

        builder.Property(v => v.Key)
            .IsRequired()
            .HasMaxLength(128);

        // No length cap on the value: a credential is short but an endpoint, a certificate or a
        // transit-encrypted ciphertext is not, and a cap here would fail a write for a reason no
        // administrator could act on.
        builder.Property(v => v.Value)
            .IsRequired();

        // One value per (tenant, module, key) — a write UPDATES, which is what makes the settings PUT a
        // merge rather than an accumulating log.
        builder.HasIndex(v => new { v.TenantId, v.ModuleId, v.Key }).IsUnique();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(v => v.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(v => v.UpdatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // Deliberately NO foreign key to ModuleActivation — see the entity's remarks: a value may be
        // configured before activation and must survive a deactivation.
    }
}
