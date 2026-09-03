using System.Security.Cryptography;
using System.Text;

namespace SimplArchive.ModuleAbi;

/// <summary>
/// The license artefact, v0 (ADRs 0740/0743): per-tenant, minimal claims, vendor-signed. The artefact
/// travels as a JSON document filed in the tenant (camelCase properties, <see cref="Signature"/> beside
/// the claims); activation reads it back, verifies it against the module's embedded key, and its
/// <see cref="SupportContractEnd"/> is what the escalate → grace → self-deactivate machinery reads.
/// </summary>
/// <remarks>
/// The signature is ECDsa P-256 / SHA-256 over <see cref="SignedPayload"/> — a fixed newline-joined
/// string rather than the JSON bytes, so the claim set, not a serializer's whitespace choices, is what is
/// signed. (ADR 0743 named Ed25519; the runtime's crypto library carries no standalone Ed25519, and a
/// native or third-party dependency for one verify call was judged worse than the curve — recorded here
/// because the ADR's algorithm note is superseded by this type.)
/// </remarks>
/// <param name="ModuleId">The module the license activates — must match <see cref="IIndustryModule.ModuleId"/>.</param>
/// <param name="TenantId">The tenant the license binds to (per-tenant, ADR 0743 — no installation identity).</param>
/// <param name="SupportContractEnd">The support contract's last day, INCLUSIVE. The module keeps running
/// through this date and the grace period beyond it, then deactivates itself (ADR 0740).</param>
/// <param name="AbiMajorVersion">The ABI major the license was issued for (ADR 0741).</param>
/// <param name="Signature">Base64 ECDsa P-256/SHA-256 signature over <see cref="SignedPayload"/>.</param>
public sealed record ModuleLicense(
    string ModuleId,
    Guid TenantId,
    DateOnly SupportContractEnd,
    int AbiMajorVersion,
    string Signature)
{
    /// <summary>
    /// The exact string the vendor signs and the core verifies: the claims newline-joined in declaration
    /// order, the tenant id in lowercase <c>D</c> format, the date as <c>yyyy-MM-dd</c>. Changing this
    /// format invalidates every issued license, so it is part of the ABI's compatibility surface.
    /// </summary>
    public string SignedPayload() =>
        $"{ModuleId}\n{TenantId:D}\n{SupportContractEnd:yyyy-MM-dd}\n{AbiMajorVersion}";

    /// <summary>
    /// Signs the claims with the vendor's private key (the vendor-tool half; the core only ever verifies).
    /// Returns a copy of this license carrying the computed <see cref="Signature"/>.
    /// </summary>
    public ModuleLicense Sign(ECDsa privateKey) => this with
    {
        Signature = Convert.ToBase64String(
            privateKey.SignData(Encoding.UTF8.GetBytes(SignedPayload()), HashAlgorithmName.SHA256)),
    };
}
