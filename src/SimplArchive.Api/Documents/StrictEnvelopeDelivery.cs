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
    SmimeMessageEnveloper enveloper)
{
    /// <summary>True when this tenant serves content only as an envelope.</summary>
    public async Task<bool> AppliesAsync(CancellationToken cancellationToken)
    {
        if (tenant.TenantId is not { } tenantId)
        {
            return false;
        }

        var name = await dbContext.Tenants
            .IgnoreQueryFilters(["TenantFilter"])
            .Where(t => t.Id == tenantId)
            .Select(t => t.Name)
            .FirstOrDefaultAsync(cancellationToken);

        return name is not null && modes.IsStrict(name);
    }

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
    /// USABLE, not merely present: a stored certificate that no longer parses would otherwise produce a rel
    /// whose request fails, which is the affordance ADR 0543 exists to prevent. Reading it costs a parse per
    /// resource build, against a column that is empty for almost every installation.
    /// </remarks>
    public async Task<string?> ReaderCertificateAsync(CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            // A service account has no certificate and no card. It is refused rather than served plaintext —
            // machine-to-machine access to a strict tenant's content is its own decision, not a default.
            return null;
        }

        var pem = await dbContext.Users
            .Where(u => u.Id == userId)
            .Select(u => u.SmimeCertificatePem)
            .FirstOrDefaultAsync(cancellationToken);

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
            // Registered but unusable. The rel disappears and the reader is told to register a current one,
            // which is a better answer than a button that fails.
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
