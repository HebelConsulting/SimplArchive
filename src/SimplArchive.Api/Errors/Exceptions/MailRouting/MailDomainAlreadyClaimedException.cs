using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.MailRouting;

// The domain is already registered (#667). Its unique index is GLOBAL by design (ADR 0628) — a domain
// identifies exactly one tenant, so a second claim would make delivery ambiguous rather than shared.
//
// It does NOT say which tenant holds it. That would answer "who else uses this product, and for which domain"
// to anyone able to type a guess, which is a tenant's configuration and none of a stranger's business. The
// refusal is the same whether the holder is you or someone else, and an administrator who cannot see it in
// their own list knows the answer is "someone else" without being told who.
public sealed class MailDomainAlreadyClaimedException(string domain)
    : MailRoutingException("MAIL_DOMAIN_ALREADY_CLAIMED", StatusCodes.Status409Conflict,
        $"'{domain}' is already registered. If your organisation owns it, it may be registered to another "
        + "tenant — contact the operator of this installation.");
