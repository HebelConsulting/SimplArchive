using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.MailRouting;

// A mailbox may not claim an existing user's personal address — no override (#703, concept default). It would
// silently divert a person's mail; if a real use case ever appears, that is a new interview, not a flag.
public sealed class UserAddressClaimNotAllowedException : MailRoutingException
{
    public UserAddressClaimNotAllowedException(string address)
        : base("USER_ADDRESS_CLAIM_NOT_ALLOWED", StatusCodes.Status409Conflict,
            $"'{address}' is a user's personal address and cannot be claimed by a mailbox.")
    {
    }
}
