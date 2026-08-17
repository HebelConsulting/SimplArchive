// PORTED from the sister project SimplCalCon (Apache-2.0, same licence), whose DAV layer is proven against
// DAVx⁵ and Apple's clients — see ADR 0621. Kept close to the original so a fix there is easy to carry over;
// only the namespace and the storage-facing types are adapted.
using System.Xml.Linq;

namespace SimplArchive.Api.CalDav.Xml;

/// <summary>
/// The set of properties a PROPFIND/REPORT asked for: an explicit list, or the
/// <c>allprop</c> / <c>propname</c> shortcuts (RFC 4918 §9.1).
/// </summary>
public sealed class PropRequest
{
    public bool AllProp { get; private init; }

    public bool PropName { get; private init; }

    public IReadOnlyList<XName> Names { get; private init; } = [];

    /// <summary>Parses a PROPFIND body root (or any element containing prop/allprop/propname).</summary>
    public static PropRequest Parse(XElement? root)
    {
        if (root is null)
        {
            // No body ⇒ treat as allprop (RFC 4918 §9.1).
            return new PropRequest { AllProp = true };
        }

        if (root.Element(DavNames.AllProp) is not null)
        {
            return new PropRequest { AllProp = true };
        }

        if (root.Element(DavNames.PropName) is not null)
        {
            return new PropRequest { PropName = true };
        }

        return FromProp(root.Element(DavNames.Prop));
    }

    /// <summary>Builds a request from an explicit <c>&lt;prop&gt;</c> element (used by REPORTs).</summary>
    public static PropRequest FromProp(XElement? prop) => new()
    {
        Names = prop?.Elements().Select(e => e.Name).ToList() ?? [],
    };
}
