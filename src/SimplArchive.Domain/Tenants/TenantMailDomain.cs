using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.Tenants;

/// <summary>A mail domain this tenant receives mail for (ADR 0628).</summary>
/// <remarks>
/// <para>
/// The envelope recipient resolves in two steps — the <b>domain</b> identifies the tenant, the <b>local part</b>
/// the user. So a domain belongs to exactly one tenant, and that is what makes it the isolation boundary here,
/// the same role the tenant query filter plays everywhere else. The unique index on
/// <see cref="NormalizedDomain"/> is therefore deliberately <b>not</b> scoped to <c>TenantId</c>: two tenants
/// claiming one domain is the failure it exists to prevent, and scoping it per tenant would permit exactly that.
/// </para>
/// <para>
/// A tenant may hold several — a real organisation has <c>example.com</c> and <c>example.de</c> — which is why
/// this is its own table rather than a column on <c>Tenant</c>.
/// </para>
/// <para>
/// <b>Tenant-scoped, but resolved before the tenant is known.</b> Every lookup that turns a recipient into a
/// tenant necessarily runs with no tenant set, so it must use
/// <c>IgnoreQueryFilters(["TenantFilter"])</c> — the same rule the login and client-id lookups follow. Left
/// unfiltered it silently matches zero rows and every message is refused as an unknown recipient.
/// </para>
/// </remarks>
public class TenantMailDomain : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>As the administrator typed it, for display.</summary>
    /// <remarks>
    /// Setting this populates <see cref="NormalizedDomain"/>, exactly as <c>User.Email</c> populates
    /// <c>NormalizedEmail</c>: always set <see cref="Domain"/>, never the normalized form, and look up by the
    /// normalized one. A mail domain is case-insensitive, so the raw value cannot be the key.
    /// </remarks>
    public required string Domain
    {
        get => _domain;
        set
        {
            _domain = value;
            NormalizedDomain = value.Trim().TrimEnd('.').ToUpperInvariant();
        }
    }

    private string _domain = string.Empty;

    /// <summary>Upper-cased, trimmed of the trailing root dot. The lookup key; never set this directly.</summary>
    public string NormalizedDomain { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}
