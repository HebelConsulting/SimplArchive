using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

// See ADR "User profile photo". 1:1 with User via a shared primary key (UserId is both PK and FK), so the
// User row never carries the blob. OnDelete Cascade from User (deleting a user removes their photo); Tenant
// FK Restrict, matching every other entity.
public class UserProfilePhotoConfiguration : IEntityTypeConfiguration<UserProfilePhoto>
{
    public void Configure(EntityTypeBuilder<UserProfilePhoto> builder)
    {
        builder.HasKey(p => p.UserId);
        builder.Property(p => p.Photo).IsRequired();

        builder.HasOne<User>()
            .WithOne()
            .HasForeignKey<UserProfilePhoto>(p => p.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(p => p.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
