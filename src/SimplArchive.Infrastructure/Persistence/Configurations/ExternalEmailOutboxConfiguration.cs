using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.Documents;
using SimplArchive.Infrastructure.Notifications;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

/// <summary>The external notice outbox (ADR 0902), as approved by the owner on 2026-10-07.</summary>
public class ExternalEmailOutboxConfiguration : IEntityTypeConfiguration<ExternalEmailOutbox>
{
    public void Configure(EntityTypeBuilder<ExternalEmailOutbox> builder)
    {
        builder.HasKey(o => o.Id);
        builder.Property(o => o.ModuleId).HasMaxLength(100).IsRequired();
        builder.Property(o => o.RecipientAddress).HasMaxLength(320).IsRequired();
        builder.Property(o => o.Subject).HasMaxLength(300).IsRequired();
        builder.Property(o => o.Body).IsRequired();
        builder.Property(o => o.ReplyTo).HasMaxLength(320);
        builder.Property(o => o.AttachmentFileName).HasMaxLength(255);
        builder.Property(o => o.Attempts).HasDefaultValue(0);

        builder.HasIndex(o => new { o.State, o.EnqueuedAt, o.Id });
        builder.HasIndex(o => o.TenantId);

        // RESTRICT: a version cannot disappear under a queued notice that will attach it.
        builder.HasOne<DocumentVersion>()
            .WithMany()
            .HasForeignKey(o => o.AttachmentVersionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
