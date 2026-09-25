namespace SimplArchive.Api.Errors.Exceptions.ExternalLinks;

/// <summary>
/// The link's tenant is in the strict encryption tier, which never serves content as plaintext (#1376).
/// </summary>
/// <remarks>
/// <para>
/// An external link serves content <b>anonymously</b>: there is no reader, so there is no certificate to
/// envelope to. Every other content surface answers a strict tenant by omitting the link and letting the
/// metadata render (ADR 0543), but here the URL <i>is</i> the response — there is nothing left to return — so
/// this is a refusal rather than an absence.
/// </para>
/// <para>
/// 409 rather than 403: it is not a rights failure, and dressing it as one would send an administrator to
/// check the recipient's permissions for a refusal that has nothing to do with them.
/// </para>
/// <para>
/// A link enveloped to a recipient certificate supplied at creation is the intended way to share out of a
/// strict tenant, and is tracked separately (#1377). Until that exists, the honest answer is no.
/// </para>
/// </remarks>
public sealed class ExternalLinkCannotServePlaintextException()
    : ExternalLinkException(
        "EXTERNAL_LINK_PLAINTEXT_REFUSED",
        StatusCodes.Status409Conflict,
        "This document's tenant is in the strict encryption tier, which never serves content as plaintext — "
        + "and an external link has no recipient certificate to envelope it to. Share it through a client that "
        + "reads with a card instead.");
