using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;

namespace SimplArchive.Infrastructure.Persistence.Configurations;

public class ChatMessageMentionConfiguration : IEntityTypeConfiguration<ChatMessageMention>
{
    public void Configure(EntityTypeBuilder<ChatMessageMention> builder)
    {
        builder.HasKey(m => m.Id);

        // Mentioning somebody twice in one message is one mention — the notification and the subscription are
        // both per-person, so a second row would mean nothing and could only cause a double notify.
        builder.HasIndex(m => new { m.ChatMessageId, m.UserId }).IsUnique();

        // "Everything I was addressed in, newest first" — the shape a mentions view would read.
        builder.HasIndex(m => new { m.TenantId, m.UserId, m.CreatedAt });

        // The one cascade: a message's mentions are part of the message. Deleting a document already clears its
        // whole thread via ChatMessage's DocumentId cascade, and this rides along with it.
        builder.HasOne<ChatMessage>()
            .WithMany()
            .HasForeignKey(m => m.ChatMessageId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict, like the thread's author FK: a mentioned user is deactivated rather than deleted, and the
        // record of who was addressed must not decide a user's fate either way.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(m => m.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(m => m.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
