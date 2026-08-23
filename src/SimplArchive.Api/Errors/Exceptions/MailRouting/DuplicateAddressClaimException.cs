using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.MailRouting;

// An address may appear on ONE mailbox's list by default (#703). The message names the mailbox already
// claiming it, because the admin's next step is deciding whether fan-out to both is intended — and the retry
// carries `confirmDuplicateClaims: true` to say so explicitly. Naming the other mailbox is not a leak: only
// holders of CanManageMailRouting reach this refusal, and they administer every mailbox anyway.
public sealed class DuplicateAddressClaimException : MailRoutingException
{
    public DuplicateAddressClaimException(string address, string claimedByMailboxName)
        : base("DUPLICATE_ADDRESS_CLAIM", StatusCodes.Status409Conflict,
            $"'{address}' is already claimed by mailbox '{claimedByMailboxName}'. Confirm the duplicate claim to deliver to both.",
            // As DATA, not only prose: the clients compose their own localized question around these, rather
            // than surfacing an English sentence to a German user (issue #424).
            new Dictionary<string, object?> { ["address"] = address, ["claimedBy"] = claimedByMailboxName })
    {
    }
}
