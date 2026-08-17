using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.CalDav;

/// <summary>What kind of change a <see cref="DavCollectionChange"/> records.</summary>
public enum DavChangeType
{
    Created = 0,
    Modified = 1,
    Removed = 2,
}

/// <summary>
/// One change to one item of a typed collection (#564 slice 3, ADR 0622) — the append-only log CTag and
/// RFC 6578 <c>sync-collection</c> are answered from. <see cref="Id"/> IS the sequence: a collection's CTag is
/// its highest id, a sync-token encodes that id, and "what changed since N" is the rows above it.
/// </summary>
/// <remarks>
/// A log rather than a counter because a sync client must be told what was REMOVED, not merely that something
/// changed — with only a counter, sync-collection degrades to "re-list everything", which is the poll it was
/// designed to replace. <see cref="ResourceName"/> is denormalised deliberately: a removed item's document may
/// be gone, and the client still has to be told which href disappeared.
/// </remarks>
public class DavCollectionChange : ITenantScoped
{
    public long Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>The typed folder the change happened in.</summary>
    public Guid FolderId { get; set; }

    public Guid DocumentId { get; set; }

    /// <summary>The item's DAV resource name at the time of the change.</summary>
    public required string ResourceName { get; set; }

    public DavChangeType ChangeType { get; set; }

    public DateTimeOffset At { get; set; }
}
