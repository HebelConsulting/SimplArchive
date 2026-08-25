using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.MailRouting;

// The value is not shaped like a mail domain (#667). A SHAPE refusal only: whether the domain exists, and
// whether this tenant owns it, are what the DNS challenge answers — refusing a syntactically fine name because
// a resolver was slow would be a worse error than the one this prevents.
//
// The offending value is named, because the mistake is almost always a recognisable one — an email address
// where a domain was asked for, a pasted URL — and naming it is what lets the person see which.
public sealed class InvalidMailDomainException(string value)
    : MailRoutingException("INVALID_MAIL_DOMAIN", StatusCodes.Status400BadRequest,
        $"'{value}' is not a mail domain. Enter the domain part on its own — for example example.com, not an "
        + "address or a web address.");
