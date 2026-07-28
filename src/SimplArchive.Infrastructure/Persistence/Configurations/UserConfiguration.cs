using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Email).IsRequired().HasMaxLength(320);
        builder.Property(u => u.NormalizedEmail).IsRequired().HasMaxLength(320);
        builder.Property(u => u.DisplayName).IsRequired().HasMaxLength(200);

        builder.HasIndex(u => new { u.TenantId, u.NormalizedEmail }).IsUnique();

        // Defense-in-depth backstop rejecting blank/space-only display names — see ADR "User DisplayName
        // validation rules". TRIM/LENGTH are standard SQL, supported identically by both PostgreSQL and
        // SQLite. Known limitation: standard SQL TRIM only strips plain space characters, not tabs/
        // newlines/other Unicode whitespace — a name consisting solely of those slips past this
        // constraint. Acceptable since this is a backstop, not the only validation: the future
        // Application-layer handler will use a full whitespace-aware check (e.g. string.IsNullOrWhiteSpace).
        builder.ToTable(t => t.HasCheckConstraint("CK_Users_DisplayName_NotBlank", "LENGTH(TRIM(\"DisplayName\")) > 0"));

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(u => u.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
