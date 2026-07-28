using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.Notifications;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

// See ADR "Notification preferences". One row per (user, type); the unique (TenantId, UserId, Type) index
// enforces at most one preference per type. Absence of a row means the default (email enabled).
public class UserNotificationPreferenceConfiguration : IEntityTypeConfiguration<UserNotificationPreference>
{
    public void Configure(EntityTypeBuilder<UserNotificationPreference> builder)
    {
        builder.HasKey(p => p.Id);

        builder.HasIndex(p => new { p.TenantId, p.UserId, p.Type }).IsUnique();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(p => p.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        // A user's preferences are their own — remove them with the user.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(p => p.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
