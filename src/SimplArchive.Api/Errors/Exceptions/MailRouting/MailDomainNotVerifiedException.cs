using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.MailRouting;

// The challenge record was not found when verification ran (#667).
//
// Not an error in the usual sense — it is the expected answer between publishing a record and DNS propagating
// it — so the message says what to do rather than what went wrong, and names the exact record. A 409 rather
// than a 400: nothing about the request is wrong, the world simply is not in the state it needs to be in yet.
//
// The challenge name rides as a Problem extension as well as in the prose, so a client can render it as a
// copyable field rather than asking the reader to retype it out of a sentence.
public sealed class MailDomainNotVerifiedException(string domain, string challengeName)
    : MailRoutingException("MAIL_DOMAIN_NOT_VERIFIED", StatusCodes.Status409Conflict,
        $"No matching TXT record was found at {challengeName}. Publish it in the DNS zone for '{domain}', then "
        + "try again — a new record can take a while to become visible.",
        new Dictionary<string, object?> { ["challengeName"] = challengeName })
{
}
