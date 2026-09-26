using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Encryption;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Documents;

/// <summary>
/// The strict tier's DELIVERY step (#1393, ADR 0828): content leaves as a CMS envelope addressed to the
/// reader's registered certificate, or it does not leave.
/// </summary>
/// <remarks>
/// <para>
/// One named class rather than logic in two controllers and a resource builder, because three questions have
/// to agree and they are asked from different places: <b>does this tenant envelope?</b>, <b>can we envelope
/// for this caller?</b> (which decides whether the rel is advertised at all), and <b>envelope these bytes</b>.
/// A rel emitted on one answer and a request served on another is the lying affordance ADR 0543 forbids.
/// </para>
/// <para>
/// <b>Why the seam cannot do this.</b> <c>EncryptingObjectStorageClient</c> refuses to presign for a strict
/// tenant (#1387) and is right to: a presigned URL hands bytes to a client the server never sees, and no URL
/// can be an envelope. But the seam has no idea WHO is asking, and an envelope is addressed to a person — so
/// the delivery step belongs at the Api layer, where the caller is known, and the seam stays the thing that
/// refuses plaintext.
/// </para>
/// <para>
/// <b>The certificate is the self-service one</b> (<c>User.SmimeCertificatePem</c>) — the same registration
/// the IMAP funnel already envelopes to, which is the precedent this whole tier is modelled on. One
/// registration means one place to look and one place to revoke. Several certificates per user, and cards
/// reached over PKCS#11, are the encryption service's own track rather than the core's.
/// </para>
/// <para>
/// Scoped, and it caches nothing across requests: a certificate can be replaced at any moment, and the cost
/// of asking is one indexed read of a row this request has usually loaded already.
/// </para>
/// </remarks>
public sealed class StrictEnvelopeDelivery(
    SimplArchiveDbContext dbContext,
    ICurrentTenantAccessor tenant,
    ICurrentUserAccessor currentUser,
    EncryptionModes modes,
    IObjectStorageClient storage,
    SmimeMessageEnveloper enveloper,
    SimplArchive.Infrastructure.Encryption.MessageEnvelopeClient registry)
{
    // MEMOISED FOR THE REQUEST, which is all this class lives for (registered scoped). The resource builder
    // asks ReaderCertificateAsync once per version, so a versions dialog asks several times — and since #1433
    // the answer can cost an outbound call to the encryption service. Per-request is also the only caching
    // that needs no policy: a certificate REVOKED between requests is re-read on the next one, so there is no
    // window in which a withdrawn reading certificate is still handed out.
    //
    // Null is a real answer here ("no usable certificate"), so the fact of having asked is its own flag.
    private bool _asked;
    private string? _certificate;

    /// <summary>True when this tenant serves content only as an envelope.</summary>
    public async Task<bool> AppliesAsync(CancellationToken cancellationToken)
    {
        if (tenant.TenantId is not { } tenantId)
        {
            return false;
        }

        var name = await TenantNameAsync(cancellationToken);
        return name is not null && modes.IsStrict(name);
    }

    /// <summary>This request's tenant NAME, which is what the mode map and the registry are both keyed by.</summary>
    /// <remarks>
    /// One query rather than two spellings of it: the mode lookup and the certificate registry ask the same
    /// question, and a second copy is how they would come to disagree about which tenant this is.
    /// </remarks>
    private async Task<string?> TenantNameAsync(CancellationToken cancellationToken) =>
        tenant.TenantId is not { } tenantId
            ? null
            : await dbContext.Tenants
                .IgnoreQueryFilters(["TenantFilter"])
                .Where(t => t.Id == tenantId)
                .Select(t => t.Name)
                .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Refuses when this tenant serves no readable content and the door cannot carry an envelope.
    /// </summary>
    /// <remarks>
    /// For the doors whose output is DERIVED from the document rather than its bytes — the text layout being
    /// the one that matters, since its words reconstruct the document in full. Those never touch the storage
    /// seam's client-facing read, so the seam cannot refuse them and the door must say so itself.
    /// </remarks>
    public async Task RefuseIfStrictAsync(string door, CancellationToken cancellationToken)
    {
        if (await AppliesAsync(cancellationToken))
        {
            throw new SimplArchive.Application.Abstractions.PlaintextContentRefusedException(door);
        }
    }

    /// <summary>
    /// The caller's usable certificate, or null — which is exactly the question "may the download and preview
    /// rels be advertised to this reader?".
    /// </summary>
    /// <remarks>
    /// <para>
    /// USABLE, not merely present: a stored certificate that no longer parses would otherwise produce a rel
    /// whose request fails, which is the affordance ADR 0543 exists to prevent.
    /// </para>
    /// <para>
    /// <b>Two sources, and the second one is why this tier worked at all (#1433).</b> The user's own column is
    /// asked first; where it is empty, the ENCRYPTION SERVICE's registry is asked. That is not a new idea — it
    /// is the precedence <c>EmailNotificationDispatcher</c> has always used — and this path was simply missing
    /// it, which made the strict tier unusable on a tenant configured the way ADR 0813 describes: self-service
    /// is closed for exactly the tenants the envelope client serves, so nothing writes the column, and the
    /// registry (which has a real <c>PUT …/certificate</c> door) was never consulted. Every reader got a
    /// permanent 409.
    /// </para>
    /// <para>
    /// Both sources are VALIDATED the same way, because "unusable" has to mean the same thing wherever the
    /// certificate came from — otherwise the rel's presence would depend on which source answered.
    /// </para>
    /// </remarks>
    public async Task<string?> ReaderCertificateAsync(CancellationToken cancellationToken)
    {
        if (_asked)
        {
            return _certificate;
        }

        _asked = true;
        _certificate = await FindCertificateAsync(cancellationToken);
        return _certificate;
    }

    private async Task<string?> FindCertificateAsync(CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            // A service account has no certificate and no card. It is refused rather than served plaintext —
            // machine-to-machine access to a strict tenant's content is its own decision, not a default.
            return null;
        }

        var reader = await dbContext.Users
            .Where(u => u.Id == userId)
            .Select(u => new { u.SmimeCertificatePem, u.Email })
            .FirstOrDefaultAsync(cancellationToken);

        if (Usable(reader?.SmimeCertificatePem) is { } own)
        {
            return own;
        }

        // The registry, for the tenants whose identities are provisioned centrally — which is precisely the
        // population whose self-service is closed. Asked only when the column is empty, so an installation that
        // registers into the core pays nothing for this.
        if (reader?.Email is not { Length: > 0 } email || await TenantNameAsync(cancellationToken) is not { } name)
        {
            return null;
        }

        return Usable(await registry.TryGetCertificatePemAsync(name, email, cancellationToken));
    }

    /// <summary>The certificate if it parses, else null — the same judgement for both sources.</summary>
    private static string? Usable(string? pem)
    {
        if (string.IsNullOrWhiteSpace(pem))
        {
            return null;
        }

        try
        {
            RecipientCertificate.Validate(pem);
            return pem;
        }
        catch (Errors.Exceptions.ExternalLinks.InvalidRecipientCertificateException)
        {
            // Present but unusable. The rel disappears and the reader is told to register a current one, which
            // is a better answer than a button that fails.
            return null;
        }
    }

    /// <summary>
    /// Reads an object through the storage seam and hands back the envelope addressed to the caller.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read THROUGH the seam, so an at-rest-encrypted object arrives decrypted (ADR 0818): the envelope is
    /// built over the document, never over its ciphertext. Plaintext exists in this process for the length of
    /// the request, which is the boundary ADR 0825 draws — never at rest, not never in memory.
    /// </para>
    /// <para>
    /// Buffers whole: CMS envelopes as one unit, the same constraint at-rest encryption already accepted.
    /// </para>
    /// </remarks>
    public async Task<byte[]> EnvelopeAsync(
        string objectKey, string fileName, string certificatePem, CancellationToken cancellationToken)
    {
        await using var content = await storage.GetObjectAsync(objectKey, cancellationToken);
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);

        return enveloper.TryEnvelopeDocument(
            buffer.ToArray(),
            WebDav.ContentTypes.ForExtension(Path.GetExtension(objectKey)),
            fileName,
            from: null,
            certificatePem)
            // Never the plaintext instead. An unreachable branch that refuses costs nothing; one that degrades
            // is where a guarantee goes silently.
            ?? throw new Errors.Exceptions.Encryption.ContentCannotBeEnvelopedException();
    }
}
