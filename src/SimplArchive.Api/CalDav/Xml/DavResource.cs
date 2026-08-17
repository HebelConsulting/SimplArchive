// PORTED from the sister project SimplCalCon (Apache-2.0, same licence), whose DAV layer is proven against
// DAVx⁵ and Apple's clients — see ADR 0621. Kept close to the original so a fix there is easy to carry over;
// only the namespace and the storage-facing types are adapted.
using System.Xml.Linq;

namespace SimplArchive.Api.CalDav.Xml;

/// <summary>
/// One resource in a Multi-Status response: its href and the full set of properties it
/// can provide. <see cref="MultiStatus"/> selects from these per the PROPFIND request,
/// reporting requested-but-absent properties as 404.
/// </summary>
public sealed class DavResource(string href)
{
    public string Href { get; } = href;

    public Dictionary<XName, XElement> Properties { get; } = [];

    /// <summary>Adds a property whose content is text, an XElement, or a set of child elements.</summary>
    public DavResource Set(XName name, object? content)
    {
        Properties[name] = new XElement(name, content);
        return this;
    }

    /// <summary>Adds a valueless property element (e.g. a marker).</summary>
    public DavResource SetEmpty(XName name)
    {
        Properties[name] = new XElement(name);
        return this;
    }
}
