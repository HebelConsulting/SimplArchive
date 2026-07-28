using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

// See ADR "MFA (interactive login, TOTP)". One-time recovery codes owned by a User; OnDelete Cascade from
// User (a user's codes go with them), Tenant FK Restrict like every other entity. Indexed by (TenantId, UserId)
// for the per-user lookup at login.
public class UserRecoveryCodeConfiguration : IEntityTypeConfiguration<UserRecoveryCode>
{
    public void Configure(EntityTypeBuilder<UserRecoveryCode> builder)
    {
        builder.HasKey(c => c.Id);
        builder.Property(c => c.CodeHash).IsRequired();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(c => c.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(c => new { c.TenantId, c.UserId });
    }
}
