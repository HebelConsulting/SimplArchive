using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.Documents;

// A catalog entry for a tag (ADR "Tag controlled vocabulary") — the admin-managed vocabulary behind the
// free-form DocumentTag strings. Rename/merge cascade-update the DocumentTag.Tag strings; a colour is looked up
// by Name for chip rendering. Retire is soft (RetiredAt set): hidden from the catalog/autocomplete + un-appliable
// when the tenant enforces the catalog, but existing usages on documents are grandfathered. ITenantScoped.
public class TagDefinition : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    // The normalized (trimmed, lowercased) tag text — matches DocumentTag.Tag; unique per tenant.
    public required string Name { get; set; }

    // An optional chip colour ("#RRGGBB" hex), validated in the controller.
    public string? Color { get; set; }

    // Null = active; set = retired (soft — kept on existing documents, not offered for new tagging).
    public DateTimeOffset? RetiredAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
