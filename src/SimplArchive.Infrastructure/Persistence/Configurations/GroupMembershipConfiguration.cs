using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.Groups;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

public class GroupMembershipConfiguration : IEntityTypeConfiguration<GroupMembership>
{
    public void Configure(EntityTypeBuilder<GroupMembership> builder)
    {
        builder.HasKey(m => new { m.UserId, m.GroupId });

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(m => m.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(m => m.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Group>()
            .WithMany()
            .HasForeignKey(m => m.GroupId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
