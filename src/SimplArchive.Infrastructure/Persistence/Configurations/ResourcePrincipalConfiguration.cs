using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.Booking;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

public class ResourcePrincipalConfiguration : IEntityTypeConfiguration<ResourcePrincipal>
{
    public void Configure(EntityTypeBuilder<ResourcePrincipal> builder)
    {
        builder.HasKey(p => p.Id);

        // One person per resource document. The reverse is deliberately NOT unique: a person holding two
        // resource documents is odd but harmless, while two people sharing one would make "whose time is
        // this?" ambiguous — which is the question the whole table exists to answer.
        builder.HasIndex(p => new { p.TenantId, p.ResourceDocumentId }).IsUnique();

        // "Which resources are mine?" — the person-calendar lookup, and the derived claimant grant's.
        builder.HasIndex(p => new { p.TenantId, p.UserId });

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(p => p.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        // The mapping dies with the document it describes: a purged dossier represents nobody. (Ordinary
        // deletes are soft and leave it standing, which is right — a restored dossier is still that person's.)
        builder.HasOne<Document>()
            .WithMany()
            .HasForeignKey(p => p.ResourceDocumentId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict, like every other user FK here: a user with bookings is not deleted out from under them.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(p => p.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
