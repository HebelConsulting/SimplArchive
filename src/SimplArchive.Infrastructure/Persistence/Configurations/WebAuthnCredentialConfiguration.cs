using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

// See ADR "WebAuthn passkeys as a second factor". Passkey credentials owned by a User; OnDelete Cascade from
// User, Tenant FK Restrict. CredentialId is globally unique (the assertion resolves the credential by it);
// indexed by (TenantId, UserId) for the per-user list at the login challenge.
public class WebAuthnCredentialConfiguration : IEntityTypeConfiguration<WebAuthnCredential>
{
    public void Configure(EntityTypeBuilder<WebAuthnCredential> builder)
    {
        builder.HasKey(c => c.Id);
        builder.Property(c => c.CredentialId).IsRequired();
        builder.Property(c => c.PublicKey).IsRequired();
        builder.Property(c => c.Name).IsRequired().HasMaxLength(100);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(c => c.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(c => c.CredentialId).IsUnique();
        builder.HasIndex(c => new { c.TenantId, c.UserId });
    }
}
