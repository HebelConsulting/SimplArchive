using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.Documents;

// One user addressed by one ChatMessage (issue #383). The row — not the message text — is the record of who was
// mentioned: it drives the auto-subscribe and the notification, and answers "who was addressed here" as a join
// rather than a regex over message bodies.
//
// The body carries a token holding the SAME user id (ChatMentions.Token), which is what lets a mention survive a
// display-name rename or two people sharing a name. The name is never stored: it is resolved for rendering, so
// there is no copy to go stale.
public class ChatMessageMention : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ChatMessageId { get; set; }

    // Who was addressed. Users only — a ServiceAccount has no in-app intray to notify, the same reason
    // DocumentSubscription is per-User.
    public Guid UserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
