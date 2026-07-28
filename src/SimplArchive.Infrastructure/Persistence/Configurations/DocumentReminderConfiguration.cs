using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

// See ADR "Document reminders". The (TenantId, RemindAt) index backs the sweep's due-reminder scan; the
// (TenantId, UserId) index backs a user's reminder list. Cascade from the target user + the document (a
// deleted user/document takes its reminders with it); the creator FK is Restrict (the reminder outlives it
// via the cascade on the target/document).
public class DocumentReminderConfiguration : IEntityTypeConfiguration<DocumentReminder>
{
    public void Configure(EntityTypeBuilder<DocumentReminder> builder)
    {
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Note).HasMaxLength(2000);

        builder.HasIndex(r => new { r.TenantId, r.RemindAt });
        builder.HasIndex(r => new { r.TenantId, r.UserId });

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(r => r.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Document>()
            .WithMany()
            .HasForeignKey(r => r.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(r => r.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(r => r.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
