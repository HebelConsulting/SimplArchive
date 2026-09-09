using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.Modules;

/// <summary>
/// One configured value for a setting an industry module declared (ADR 0772) — per tenant, per module, per
/// key.
/// </summary>
/// <remarks>
/// <para>
/// A ROW PER VALUE rather than one blob per module: the model has to stay provider-agnostic across
/// PostgreSQL and SQLite, which rules out a JSON column, and the unique index is then what enforces "one
/// value per key" instead of code that merges a document.
/// </para>
/// <para>
/// <b><see cref="IsSecret"/> records how the value was STORED, not what the module currently declares.</b> A
/// module can change a declaration between the write and the read; a reader that consulted the live
/// declaration would try to decrypt plaintext, or hand out ciphertext, after such a change. The row carries
/// its own answer.
/// </para>
/// <para>
/// There is deliberately <b>no foreign key to <see cref="ModuleActivation"/></b> — the
/// <c>LicenseDocumentId</c> precedent. A credential may legitimately be configured before a module is
/// activated, and must survive a deactivation: an FK would delete a customer's account details as a side
/// effect of turning a feature off. Removal stays an explicit act.
/// </para>
/// </remarks>
public class ModuleSettingValue : ITenantScoped, IConcurrencyTracked
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>The assembly-keyed module id, as on <see cref="ModuleActivation"/>.</summary>
    public string ModuleId { get; set; } = string.Empty;

    /// <summary>The declared <c>ModuleSetting.Key</c> this value belongs to.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Plaintext, or transit-encrypted ciphertext when <see cref="IsSecret"/>.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>Whether <see cref="Value"/> is encrypted — see the remarks on why this is stored.</summary>
    public bool IsSecret { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedByUserId { get; set; }

    public Guid ConcurrencyToken { get; set; }
}
