using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.Users;

// A user's profile photo (ADR "User profile photo"), stored 1:1 with the User via a shared primary key so
// the User row itself never carries the image blob. The bytes are a normalized 256×256 PNG (the clients
// crop + normalize before upload; the Api validates the PNG). ITenantScoped so the tenant query filter
// applies like every other entity.
public class UserProfilePhoto : ITenantScoped
{
    // Primary key AND foreign key to User.Id (shared-primary-key 1:1); a user has at most one photo.
    public Guid UserId { get; set; }

    public Guid TenantId { get; set; }

    public required byte[] Photo { get; set; }

    // For cache-busting the corner <img> when the photo changes.
    public DateTimeOffset UpdatedAt { get; set; }
}
