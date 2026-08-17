// PORTED from the sister project SimplCalCon (Apache-2.0, same licence) — see ADR 0621. Its ACL is a flags
// enum; SimplArchive's is EffectiveRights, so the MAPPING is adapted while the emitted privilege set is
// deliberately identical: a client checks BIND before offering "new item" and UNBIND before offering delete,
// so reporting only <write/> (as the pre-port middleware did) leaves capable clients read-only.
using System.Xml.Linq;
using SimplArchive.Api.CalDav.Xml;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Api.CalDav;

internal static class DavPrivileges
{
    internal static IEnumerable<XElement> From(EffectiveRights rights)
    {
        var privileges = new List<XName>();

        if (rights.CanSee || rights.CanReadContent)
        {
            privileges.Add(DavNames.Read);
        }

        if (rights.CanEditContent)
        {
            privileges.Add(DavNames.Write);
            privileges.Add(DavNames.WriteContent);
            privileges.Add(DavNames.Bind);
            privileges.Add(DavNames.Unbind);
        }

        if (rights.CanEditIndexData)
        {
            privileges.Add(DavNames.WriteProperties);
        }

        return privileges.Distinct().Select(p => new XElement(DavNames.Privilege, new XElement(p)));
    }
}
