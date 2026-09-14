using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SimplArchive.Domain.Modules;
using SimplArchive.ModuleAbi;

namespace SimplArchive.Infrastructure.Modules;

/// <summary>
/// Parses and verifies a module license artefact (ADRs 0740/0743): the JSON claims from the filed
/// document, the ECDsa P-256 signature against the module's embedded key, and the claim checks (module,
/// tenant, ABI major). Pure functions — verification must be provable without a host.
/// </summary>
public static class ModuleLicenseVerifier
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Parses the filed document's content into the license record, refusing unreadable input
    /// with the reason rather than a bare parse failure.</summary>
    public static ModuleLicense Parse(string licenseJson)
    {
        try
        {
            return JsonSerializer.Deserialize<ModuleLicense>(licenseJson, JsonOptions)
                ?? throw ModuleLicenseException.Malformed("the document is empty.");
        }
        catch (JsonException exception)
        {
            throw ModuleLicenseException.Malformed(exception.Message);
        }
    }

    /// <summary>
    /// Verifies every claim a license makes, throwing the precise <see cref="ModuleLicenseException"/> on
    /// the first that fails. Order is deliberate: the signature first — an unsigned artefact's other
    /// claims are not worth reading — then module, tenant, ABI major.
    /// </summary>
    /// <returns>
    /// The <see cref="KeyThumbprint"/> of the key that accepted the signature (ABI 0.25, ADR 0793) — the
    /// activation records it, so after a key compromise the affected activations are a query rather than an
    /// audit trawl.
    /// </returns>
    public static string Verify(ModuleLicense license, IIndustryModule module, Guid tenantId)
    {
        // ANY of the module's keys may have signed it — the list is the vendor's rotation overlap window
        // (ADR 0793). Tried in declaration order; the first that accepts wins, and its identity is returned.
        if (VerifyingKey(license, module.LicenseVerifyKeysPem) is not { } verifyingKeyPem)
        {
            throw ModuleLicenseException.BadSignature(module.ModuleId);
        }

        if (!string.Equals(license.ModuleId, module.ModuleId, StringComparison.Ordinal))
        {
            throw ModuleLicenseException.WrongModule(license.ModuleId, module.ModuleId);
        }

        if (license.TenantId != tenantId)
        {
            throw ModuleLicenseException.WrongTenant();
        }

        if (license.AbiMajorVersion != ModuleAbiVersion.Major)
        {
            throw ModuleLicenseException.AbiMismatch(license.AbiMajorVersion, ModuleAbiVersion.Major);
        }

        return KeyThumbprint(verifyingKeyPem);
    }

    /// <summary>
    /// A stable, short identity for a verify key: the SHA-256 of its <c>SubjectPublicKeyInfo</c> DER, as
    /// lowercase hex. Of the KEY, not of the PEM text — so whitespace, line endings or a re-export do not
    /// change it, and two modules shipping the same key agree on its name. Empty when the PEM cannot be read,
    /// which only a key that verifies nothing can be.
    /// </summary>
    public static string KeyThumbprint(string verifyKeyPem)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(verifyKeyPem);
            return Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException)
        {
            return string.Empty;
        }
    }

    /// <summary>The first listed key whose signature check passes, or null when none does.</summary>
    private static string? VerifyingKey(ModuleLicense license, IReadOnlyList<string> verifyKeysPem) =>
        verifyKeysPem.FirstOrDefault(pem => SignatureVerifies(license, pem));

    private static bool SignatureVerifies(ModuleLicense license, string verifyKeyPem)
    {
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(license.Signature);
        }
        catch (FormatException)
        {
            return false;
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(verifyKeyPem);
            return key.VerifyData(Encoding.UTF8.GetBytes(license.SignedPayload()), signature, HashAlgorithmName.SHA256);
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException)
        {
            // A module shipping a broken verify key fails every license — the right failure mode, since a
            // key the vendor cannot get right is a license nobody should trust.
            return false;
        }
    }
}
