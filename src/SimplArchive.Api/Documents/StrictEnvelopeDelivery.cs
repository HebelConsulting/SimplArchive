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
    SimplArchive.Infrastructure.Encryption.MessageEnvelopeClient registry,
    SimplArchive.Infrastructure.Storage.AtRestKeyService atRestKeys,
    SimplArchive.Infrastructure.Modules.ModuleReaderCertificates moduleCertificates,
    Microsoft.Extensions.Logging.ILogger<StrictEnvelopeDelivery> logger)
{
    // MEMOISED FOR THE REQUEST, which is all this class lives for (registered scoped). The resource builder
    // asks ReaderCertificateAsync once per version, so a versions dialog asks several times — and since #1433
    // the answer can cost an outbound call to the encryption service. Per-request is also the only caching
    // that needs no policy: a certificate REVOKED between requests is re-read on the next one, so there is no
    // window in which a withdrawn reading certificate is still handed out.
    //
    // Null is a real answer here ("no usable certificate"), so the fact of having asked is its own flag.
    private bool _asked;
    private IReadOnlyList<string> _certificates = [];

    // Null is a real answer here too (no tenant, or a tenant row that is gone), so the fact of having asked
    // is again its own flag rather than being inferred from the value.
    private bool _namedTenant;
    private string? _tenantName;

    // WHY the set is what it is, remembered beside it (#1411, ADR 0859). The rel decision only needs the
    // set — absent is absent, whatever the cause — but the REFUSAL needs the cause, or a reader whose
    // installation is broken is told to register a certificate they already have.
    private SimplArchive.Infrastructure.Modules.ReaderCertificateOutcome _outcome =
        SimplArchive.Infrastructure.Modules.ReaderCertificateOutcome.NoModule;

    /// <summary>True when this tenant DELIVERS content as an envelope.</summary>
    /// <remarks>
    /// Deliberately not the same question as <see cref="RefusesPlaintextDoorsAsync"/>, and they came apart with
    /// the delivery tiers (#1411): <c>SealedDeliveryPermissive</c> envelopes for readers who have a certificate
    /// while every other door goes on serving as before. One predicate answering both is what this whole
    /// refactor existed to end, and it survived here for one caller until the owner asked what a permissive
    /// tenant serves a reader with no certificate.
    /// </remarks>
    public async Task<bool> AppliesAsync(CancellationToken cancellationToken)
    {
        if (tenant.TenantId is not { } tenantId)
        {
            return false;
        }

        var name = await TenantNameAsync(cancellationToken);
        return name is not null && modes.DeliversEnvelopes(name);
    }

    /// <summary>True when no door of this tenant's may serve readable bytes.</summary>
    public async Task<bool> RefusesPlaintextDoorsAsync(CancellationToken cancellationToken)
    {
        if (tenant.TenantId is not { } tenantId)
        {
            return false;
        }

        var name = await TenantNameAsync(cancellationToken);
        return name is not null && modes.RefusesPlaintextDoors(name);
    }

    /// <summary>This request's tenant NAME, which is what the mode map and the registry are both keyed by.</summary>
    /// <remarks>
    /// <para>
    /// One query rather than two spellings of it: the mode lookup and the certificate registry ask the same
    /// question, and a second copy is how they would come to disagree about which tenant this is.
    /// </para>
    /// <para>
    /// MEMOISED FOR THE REQUEST, like the certificate set above and for the same reason — this class is
    /// registered scoped and a request's tenant cannot change underneath it. It matters now that both
    /// membership questions are asked PER VERSION (ADR 0865): a versions dialog would otherwise spend two
    /// indexed reads per row re-learning the name of the tenant it is already scoped to.
    /// </para>
    /// </remarks>
    private async Task<string?> TenantNameAsync(CancellationToken cancellationToken)
    {
        if (_namedTenant)
        {
            return _tenantName;
        }

        _tenantName = tenant.TenantId is not { } tenantId
            ? null
            : await dbContext.Tenants
                .IgnoreQueryFilters(["TenantFilter"])
                .Where(t => t.Id == tenantId)
                .Select(t => t.Name)
                .FirstOrDefaultAsync(cancellationToken);
        _namedTenant = true;
        return _tenantName;
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
        // The DOOR question, not the delivery one. Asking AppliesAsync refused this door on a
        // SealedDeliveryPermissive tenant while every other door served — the posture inverted for exactly the
        // door that most needs to follow it, since the overlay's word coordinates reconstruct the document.
        if (await RefusesPlaintextDoorsAsync(cancellationToken))
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
    public async Task<IReadOnlyList<string>> ReaderCertificatePemsAsync(CancellationToken cancellationToken)
    {
        if (_asked)
        {
            return _certificates;
        }

        _asked = true;
        _certificates = await FindCertificatesAsync(cancellationToken);
        return _certificates;
    }

    /// <summary>
    /// The reader's certificates, or the RIGHT refusal for why there are none (#1411, ADR 0859).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Here rather than at the controller, because this is the only place that knows WHY the set is empty.
    /// The controller used to throw <c>ContentCannotBeEnveloped</c> for every empty set, which is how a
    /// reader on an installation with a broken or unlicensed module came to be told to "register a current
    /// certificate and try again" — wrong, and unactionable, since the fix is an administrator's.
    /// </para>
    /// <para>
    /// ADR 0842 requires four refusals to stay distinguishable because they have different fixes. Three are
    /// answerable here. The fourth — no certificate enrolled versus every certificate filtered out — is known
    /// only to the module, which returns an empty set either way, so those two still share a message until
    /// the ABI carries a reason.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<string>> RequireReaderCertificatesAsync(CancellationToken cancellationToken)
    {
        var certificates = await ReaderCertificatePemsAsync(cancellationToken);

        return certificates.Count > 0 ? certificates : throw RefusalFor(_outcome);
    }

    /// <summary>
    /// The refusal that names the cause, for an empty certificate set.
    /// </summary>
    /// <remarks>
    /// Static and internal so the mapping can be ASSERTED. Constructing this service takes eight
    /// collaborators and a database, none of which the mapping depends on — and a cause-to-message table
    /// nobody tests is exactly how the collapsed message survived as long as it did.
    /// </remarks>
    internal static Exception RefusalFor(SimplArchive.Infrastructure.Modules.ReaderCertificateOutcome outcome) => outcome switch
    {
        SimplArchive.Infrastructure.Modules.ReaderCertificateOutcome.AskFailed =>
            new Errors.Exceptions.Encryption.ReaderCertificatesUnavailableException(),

        SimplArchive.Infrastructure.Modules.ReaderCertificateOutcome.LicenceLapsed =>
            new Errors.Exceptions.Encryption.EncryptionModuleNotLicensedException(),

        // A module answered "none", or there is no module and neither the column nor the registry had one.
        // Both are genuinely "you have no usable certificate here", which the reader can act on.
        _ => new Errors.Exceptions.Encryption.ContentCannotBeEnvelopedException(),
    };

    private async Task<IReadOnlyList<string>> FindCertificatesAsync(CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            // A service account has no certificate and no card. It is refused rather than served plaintext —
            // machine-to-machine access to a strict tenant's content is its own decision, not a default.
            return [];
        }

        // THE MODULE, where one answers, is the ONLY source (ADR 0842). Not a fallback chain and not a union:
        // a union means a certificate REVOKED in the module still opens documents while a copy lingers in the
        // column, which is a revocation that does not revoke.
        //
        // Every outcome but NoModule closes the core's own sources — a lapsed licence and a failure to ask
        // have not said "this reader has none" — and each is remembered, because ADR 0842 requires the
        // refusals to stay distinguishable and they have different fixes (ADR 0859).
        var fromModule = await moduleCertificates.ForAsync(userId, cancellationToken);
        _outcome = fromModule.Outcome;
        if (fromModule.ModuleSpoke)
        {
            // THE ANSWER THIS TIER TURNS ON, SAID OUT LOUD (#1498). An empty set here withdraws the download
            // and preview rels — correctly, since nothing can be enveloped — and that withdrawal is silent
            // by construction: the reader sees a document with no content affordance and no reason. It cost
            // an evening's guessing, because every layer looked right and nothing anywhere said "the module
            // answered, and it answered none".
            logger.LogDebug(
                "The module answered which certificates user {UserId} is addressed by: {Count}. "
                + "The enveloping rels are {State}.",
                userId, fromModule.Certificates.Count,
                fromModule.Certificates.Count > 0 ? "advertised" : "WITHDRAWN");

            return [.. fromModule.Certificates.Select(c => c.CertificatePem)];
        }

        // Not "no certificate": the module did not speak, and the three ways that happens have three
        // different fixes (ADR 0859). Named here because the core's own sources are about to be tried and
        // whichever answer they give will look like the module's.
        logger.LogDebug(
            "No module answered for user {UserId} ({Outcome}); falling back to the core's own sources.",
            userId, _outcome);

        var reader = await dbContext.Users
            .Where(u => u.Id == userId)
            .Select(u => new { u.SmimeCertificatePem, u.Email })
            .FirstOrDefaultAsync(cancellationToken);

        if (Usable(reader?.SmimeCertificatePem) is { } own)
        {
            return [own];
        }

        // The registry, for the tenants whose identities are provisioned centrally — which is precisely the
        // population whose self-service is closed. Asked only when the column is empty, so an installation that
        // registers into the core pays nothing for this.
        if (reader?.Email is not { Length: > 0 } email || await TenantNameAsync(cancellationToken) is not { } name)
        {
            return [];
        }

        return Usable(await registry.TryGetCertificatePemAsync(name, email, cancellationToken)) is { } registered
            ? [registered]
            : [];
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
        string objectKey, string fileName, IReadOnlyList<string> certificatePems, CancellationToken cancellationToken)
    {
        return await TryEnvelopeAsync(objectKey, fileName, certificatePems, cancellationToken)
            // Never the plaintext instead. An unreachable branch that refuses costs nothing; one that degrades
            // is where a guarantee goes silently.
            ?? throw new Errors.Exceptions.Encryption.ContentCannotBeEnvelopedException();
    }

    /// <summary>
    /// The same envelope, answering null where it could not be built — for a caller with its own refusal.
    /// </summary>
    /// <remarks>
    /// The split exists because the external-link door refuses in its own words (an outsider is told
    /// something different from a signed-in reader), and the alternative was a second copy of the reroute —
    /// which is how one path would come to consult the service and the other not. Null means "could not"; a
    /// service that is DOWN throws instead, because those are different facts and ADR 0859 is the standing
    /// argument for keeping such refusals distinguishable.
    /// </remarks>
    public async Task<byte[]?> TryEnvelopeAsync(
        string objectKey, string fileName, IReadOnlyList<string> certificatePems, CancellationToken cancellationToken)
    {
        var contentType = WebDav.ContentTypes.ForExtension(Path.GetExtension(objectKey));

        // WHERE THE OBJECT IS WRAPPED AT REST, THE SERVICE DOES BOTH HALVES (ADR 0862) — it already holds
        // the KEK, and enveloping a DECRYPTED blob is the last step of a decryption rather than separate
        // work. This process then never sees the data key or the cleartext on a read.
        //
        // Asked per OBJECT, not per tenant: storage is mixed state (ADR 0818), so a strict tenant filed
        // before encryption was configured still has plaintext objects, and those take the local path below
        // exactly as they always did.
        if (await CiphertextOf(objectKey, cancellationToken) is { } wrapped)
        {
            return await EnvelopeInTheServiceAsync(
                objectKey, fileName, contentType, wrapped, certificatePems, cancellationToken);
        }

        await using var content = await storage.GetObjectAsync(objectKey, cancellationToken);
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);

        return enveloper.TryEnvelopeDocument(
            buffer.ToArray(), contentType, fileName, from: null, certificatePems);
    }

    /// <summary>
    /// The object's wrapped DEK and KEK generation, or null when it is not encrypted at rest.
    /// </summary>
    /// <remarks>
    /// Read from the object's own METADATA, which is where at-rest encryption keeps them (ADR 0818) — so
    /// this asks the object what it is rather than asking configuration what it ought to be. That
    /// distinction is the whole reason mixed state is safe.
    /// </remarks>
    private async Task<(string WrappedDek, string Generation)?> CiphertextOf(
        string objectKey, CancellationToken cancellationToken)
    {
        if (!atRestKeys.Enabled)
        {
            return null;
        }

        var info = await storage.GetObjectInfoAsync(objectKey, cancellationToken);
        if (!info.Metadata.TryGetValue(
                SimplArchive.Infrastructure.Storage.EncryptingObjectStorageClient.WrappedDekKey, out var wrapped)
            || !info.Metadata.TryGetValue(
                SimplArchive.Infrastructure.Storage.EncryptingObjectStorageClient.KekGenerationKey, out var generation))
        {
            return null;
        }

        // THE KEY-IDENTITY CHECK HAS TO BE REPEATED HERE, and the reason is structural rather than defensive:
        // on this tier the core does NOT unwrap (ADR 0862 hands the wrapped DEK and a ciphertext address to
        // the service, which decrypts and envelopes), so this read never reaches AtRestKeyService's oracle
        // where ADR 0867's check otherwise sits. Without this the one tier where the loss matters most —
        // the one holding WORM content that can never be re-wrapped — would be the one tier that reported it
        // as a generic service refusal inviting a retry.
        if (info.Metadata.TryGetValue(
                SimplArchive.Infrastructure.Storage.EncryptingObjectStorageClient.KekThumbprintKey,
                out var stamped)
            && stamped is { Length: > 0 }
            && await atRestKeys.GenerationThumbprintAsync(generation, cancellationToken) is { Length: > 0 } held
            && !string.Equals(stamped, held, StringComparison.OrdinalIgnoreCase))
        {
            throw new SimplArchive.Infrastructure.Storage.AtRestKeyChangedException(
                objectKey, generation, stamped, held);
        }

        return (wrapped, generation);
    }

    /// <summary>
    /// Hands the ciphertext's ADDRESS to the encryption service and wraps the envelope it returns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bytes go storage → service directly; this process passes a short-lived presigned URL and never
    /// touches them, which also keeps the standing rule that the Api does not proxy file bytes. The presign
    /// is the ciphertext one, which refuses outright for an unencrypted object — so it cannot become the
    /// plaintext door this tier closes.
    /// </para>
    /// <para>
    /// The INNER headers describe the entity inside the envelope, and they are what the local path puts on
    /// its attachment part: a reader who opens either artefact finds a part with the same type and filename.
    /// Sending them is necessary rather than cosmetic — without them the service envelopes bare bytes, and
    /// the reader's client shows an anonymous blob with the filename surviving only in the outer subject.
    /// </para>
    /// <para>
    /// <b>No transfer encoding is declared on that inner part</b>, so its content is the raw bytes. MimeKit
    /// reads it back byte-identically (there is a test), and the alternative would be a third header the
    /// service does not accept. Worth knowing if a strict third-party client ever objects: the fix is a
    /// <c>binary</c> encoding declared alongside the other two, which is a service-side parameter.
    /// </para>
    /// </remarks>
    private async Task<byte[]?> EnvelopeInTheServiceAsync(
        string objectKey,
        string fileName,
        string contentType,
        (string WrappedDek, string Generation) wrapped,
        IReadOnlyList<string> certificatePems,
        CancellationToken cancellationToken)
    {
        var ciphertextUrl = await storage.GetPresignedCiphertextUrlAsync(
            objectKey, CiphertextFetchWindow, cancellationToken);

        byte[] envelope;
        try
        {
            envelope = await atRestKeys.DecryptedEnvelopeAsync(
                wrapped.WrappedDek,
                wrapped.Generation,
                ciphertextUrl,
                certificatePems,
                innerContentType: contentType,
                innerContentDisposition: $"attachment; filename=\"{fileName.Replace("\"", string.Empty)}\"",
                cancellationToken);
        }
        catch (Infrastructure.Storage.EnvelopeServiceRefusedException refusal) when (refusal.OurRequest)
        {
            // THE SERVICE ANSWERED, and what it said is that the request is unacceptable — so this is not an
            // outage and must not be reported as one. Retrying a 4xx answers the same way forever, and the
            // reader following "try again shortly" learns nothing. The service's own words are already in the
            // log at Error (AtRestKeyService); this is the refusal that matches them.
            throw new Errors.Exceptions.Encryption.ContentEnvelopeRefusedException();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
            or Infrastructure.Storage.EnvelopeServiceRefusedException)
        {
            // NOT null, and not a plaintext fall-back. Null here would reach the caller's "could not
            // envelope" refusal, which tells a reader to re-register a certificate that is perfectly
            // fine — the mistaken advice ADR 0859 was written to stop. This says the SERVICE is down,
            // and there is no plaintext available to fall back to even if that were wanted (ADR 0862).
            //
            // A 5xx from the service lands here too: it answered, but about itself, which is the same fact
            // as not answering at all as far as a client's next move goes.
            throw new Errors.Exceptions.Encryption.EnvelopeServiceUnavailableException();
        }

        return enveloper.TryWrapEnvelope(envelope, fileName, from: null, certificatePems);
    }

    /// <summary>
    /// How long the ciphertext's address stays valid — long enough for one fetch by a sidecar, no longer.
    /// </summary>
    /// <remarks>
    /// Deliberately much shorter than a browser download's window: this URL is handed to a process on the
    /// same network which fetches immediately, so a generous expiry would only widen the period in which a
    /// leaked address is useful — and it addresses ciphertext, not a document, which is why minutes rather
    /// than seconds is still a reasonable floor for a slow store.
    /// </remarks>
    private static readonly TimeSpan CiphertextFetchWindow = TimeSpan.FromMinutes(2);
}
