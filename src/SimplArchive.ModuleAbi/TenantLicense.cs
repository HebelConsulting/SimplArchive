using System.Security.Cryptography;
using System.Text;

namespace SimplArchive.ModuleAbi;

/// <summary>
/// The tenant's licence, v1 (ADR 0845): ONE artefact naming every paid module that tenant holds, with ONE
/// expiry, vendor-signed. Filed in the tenant as a JSON document wearing the core <c>License</c> mask;
/// activation reads it back, verifies it against the module's embedded key (ADR 0793), and its
/// <see cref="SupportContractEnd"/> drives the escalate → grace → self-deactivate ladder (ADR 0740).
/// </summary>
/// <remarks>
/// <para>
/// <b>Replaces the per-module <c>ModuleLicense</c>, which is why the ABI major moved to 1.</b> A record's
/// constructor changing shape is the exact form that crash-looped the kiosk for 1h34m (ADR 0789, #1147) —
/// so this does not change that record, it replaces it, behind the gate the loader checks. A module compiled
/// against ABI 0 is now REFUSED at load, cleanly, rather than admitted and failed inside.
/// </para>
/// <para>
/// <b>ONE expiry, enforced by construction.</b> A single renewal date is the requirement (owner, 2026-09-29),
/// and one claim is what makes it true rather than merely intended: the modules in a licence cannot drift
/// apart, because there is nothing for them to drift in.
/// </para>
/// <para>
/// <b>The module ids are EXPLICIT.</b> A blanket entitlement would make what a customer paid for a consequence
/// of what an administrator happened to install, and leave nothing to point at when the two disagree.
/// </para>
/// <para>
/// The signature is ECDsa P-256 / SHA-256 over <see cref="SignedPayload"/> — a fixed newline-joined string
/// rather than the JSON bytes, so the claim set, not a serializer's whitespace choices, is what is signed.
/// </para>
/// </remarks>
/// <param name="ModuleIds">Every module this licence entitles. Order is irrelevant to the reader and fixed in
/// <see cref="SignedPayload"/>, so a licence signed from a differently-ordered list still verifies.</param>
/// <param name="TenantId">The tenant this binds to — per-tenant, no installation identity. A SERVICE is
/// licensed per installation by its own artefact instead (ADRs 0841/0845).</param>
/// <param name="SupportContractEnd">The support contract's last day, INCLUSIVE, for every module named.</param>
/// <param name="AbiMajorVersion">The ABI major the licence was issued for (ADR 0741).</param>
/// <param name="Signature">Base64 ECDsa P-256/SHA-256 signature over <see cref="SignedPayload"/>.</param>
public sealed record TenantLicense(
    IReadOnlyList<string> ModuleIds,
    Guid TenantId,
    DateOnly SupportContractEnd,
    int AbiMajorVersion,
    string Signature)
{
    /// <summary>
    /// <b>Record equality is NOT meaningful on this type</b>: <see cref="ModuleIds"/> is a list, and a record
    /// compares it by REFERENCE — so a licence parsed back from its own JSON never equals the one it came
    /// from. Compare <see cref="SignedPayload"/> and <see cref="Signature"/> when identity matters; those are
    /// what the vendor signed and what the core verifies, which is the only identity this artefact has.
    /// </summary>
    /// <summary>True when this licence entitles <paramref name="moduleId"/>.</summary>
    /// <remarks>
    /// Ordinal and case-sensitive, like every other module-id comparison on this ABI: an id is a key, not a
    /// display name, and a case-insensitive match here would admit a module the vendor never named.
    /// </remarks>
    public bool Entitles(string moduleId) => ModuleIds.Contains(moduleId, StringComparer.Ordinal);

    /// <summary>
    /// The exact string the vendor signs and the core verifies: the claims newline-joined in declaration
    /// order, module ids SORTED and comma-joined, the tenant id in lowercase <c>D</c> format, the date as
    /// <c>yyyy-MM-dd</c>. Changing this format invalidates every issued licence, so it is part of the ABI's
    /// compatibility surface.
    /// </summary>
    /// <remarks>
    /// Sorted deliberately: the vendor tool and the verifier must agree byte-for-byte, and the order a list
    /// happens to arrive in is exactly the kind of incidental difference that would make a valid licence fail
    /// for one customer and not another.
    /// </remarks>
    public string SignedPayload() =>
        $"{string.Join(",", ModuleIds.Order(StringComparer.Ordinal))}\n{TenantId:D}\n"
        + $"{SupportContractEnd:yyyy-MM-dd}\n{AbiMajorVersion}";

    /// <summary>
    /// Signs the claims with the vendor's private key (the vendor-tool half; the core only ever verifies).
    /// </summary>
    public TenantLicense Sign(ECDsa privateKey) => this with
    {
        Signature = Convert.ToBase64String(
            privateKey.SignData(Encoding.UTF8.GetBytes(SignedPayload()), HashAlgorithmName.SHA256)),
    };

    /// <summary>True when <paramref name="publicKey"/> signed exactly these claims.</summary>
    public bool VerifiedBy(ECDsa publicKey)
    {
        try
        {
            return publicKey.VerifyData(
                Encoding.UTF8.GetBytes(SignedPayload()), Convert.FromBase64String(Signature),
                HashAlgorithmName.SHA256);
        }
        catch (FormatException)
        {
            return false;   // a Signature that is not base64 at all is a failed verification, not a crash
        }
    }
}
