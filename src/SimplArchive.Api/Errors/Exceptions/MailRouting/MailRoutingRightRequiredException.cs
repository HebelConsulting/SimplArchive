using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.MailRouting;

// Changing where a tenant's mail goes needs CanManageMailRouting (#703): writing a Mailbox's address list,
// and deleting or restoring a mailbox. A typed 403 rather than a bare Forbid() so the refusal says WHICH
// right was missing — the caller may well hold every other right on the document.
public sealed class MailRoutingRightRequiredException : MailRoutingException
{
    public MailRoutingRightRequiredException(string action)
        : base("MAIL_ROUTING_RIGHT_REQUIRED", StatusCodes.Status403Forbidden,
            $"{action} requires the manage-mail-routing right.")
    {
    }
}
