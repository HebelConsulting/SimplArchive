namespace SimplArchive.ModuleAbi;

/// <summary>
/// One certificate a reader is addressed by (core ADR 0842) — the END-ENTITY certificate, never a chain.
/// </summary>
/// <param name="CertificatePem">The reader's public certificate, PEM-encoded. Exactly one certificate: a
/// module that stores a chain resolves the leaf itself, because a bundle's ORDER is conventional and picking
/// the wrong member means enveloping to the ISSUER's key — the CA could open the document and the reader
/// could not.</param>
/// <param name="Label">What the holder calls this key — "YubiKey 5C", "work laptop". Carried so a refusal can
/// name WHICH certificate was unusable; a bare "no certificate" cannot tell an administrator that three were
/// enrolled and all expired.</param>
/// <param name="NotAfter">When it expires. The module has already filtered on it (core ADR 0842 decision 3);
/// this is here so the core can SAY so rather than re-decide it.</param>
public sealed record ReaderCertificate(string CertificatePem, string Label, DateTimeOffset NotAfter);

/// <summary>
/// What a module is handed when the core asks which certificates a reader is addressed by.
/// </summary>
/// <param name="UserId">The reader, by the core's user id.</param>
/// <param name="Archive">The module's facade, resolved per call.</param>
/// <param name="Services">The host's provider, for the module's own registrations.</param>
/// <remarks>
/// Keyed on the ID rather than the e-mail address deliberately (core ADR 0842): S/MIME binds a certificate to
/// an address, but an address CHANGES — and <c>User.Email</c>'s setter rewrites the normalized form with
/// nothing downstream noticing (core ADR 0150), so an e-mail key would silently orphan every certificate its
/// owner holds.
/// </remarks>
public sealed record ReaderCertificateContext(
    Guid UserId, IModuleArchiveFacade Archive, IServiceProvider Services);
