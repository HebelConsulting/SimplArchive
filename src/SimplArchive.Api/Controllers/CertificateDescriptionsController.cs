using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SimplArchive.Api.Documents;
using SimplArchive.Api.Hypermedia;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// Says who an X.509 certificate belongs to — subject and SHA-256 fingerprint (#1390, ADR 0827).
/// </summary>
/// <remarks>
/// <para>
/// It exists because <b>Blazor WASM cannot parse a certificate at all</b>: `X509Certificate2` throws
/// `PlatformNotSupportedException` in the browser runtime, measured rather than assumed. So the web client
/// could not show a sharer who they were about to address a document to — the only check available to them,
/// since a PEM is unreadable by eye and a wrong key cannot be undone once the link has been sent.
/// </para>
/// <para>
/// <b>Both clients call it, and neither parses locally.</b> The desktop could do it in-process, and doing so
/// would leave two implementations answering one question — which is how the two would come to disagree, and
/// how what a sharer approved could stop matching what an investigator later reads. This endpoint shares its
/// implementation with the create path, so the description, the validation and the audit record are one
/// answer computed once (<see cref="RecipientCertificate"/>).
/// </para>
/// <para>
/// <b>Nothing is stored and nothing is decided here.</b> It is a POST because the input is a certificate that
/// does not fit in a URL and must not appear in one; the response is derived from the request alone, and the
/// same body always yields the same answer. Authenticated, because there is no reason to offer a parsing
/// oracle to the internet even a harmless one.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/certificate-descriptions")]
[Authorize]
public class CertificateDescriptionsController : ControllerBase
{
    public class DescribeCertificateRequest
    {
        public string? CertificatePem { get; set; }
    }

    public class CertificateDescriptionResource : HypermediaResource
    {
        /// <summary>The subject as the issuer wrote it — what a human recognises.</summary>
        public string Subject { get; set; } = string.Empty;

        /// <summary>SHA-256 over the DER — what identifies the exact key.</summary>
        public string Fingerprint { get; set; } = string.Empty;
    }

    /// <summary>Describes a PEM, or refuses it with the same error the create path would give.</summary>
    /// <remarks>
    /// Refusing HERE with the same exception is the point of sharing the implementation: a certificate this
    /// endpoint accepts is one that creating a link will accept, so a sharer cannot be told "that looks fine"
    /// and then refused a moment later.
    /// </remarks>
    [HttpPost]
    public IActionResult Describe([FromBody] DescribeCertificateRequest request)
    {
        var described = RecipientCertificate.Validate(request.CertificatePem ?? string.Empty);

        return Ok(new CertificateDescriptionResource
        {
            Subject = described.Subject,
            Fingerprint = described.Fingerprint,
        });
    }
}
