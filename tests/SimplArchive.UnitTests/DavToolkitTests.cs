using System.Xml.Linq;
using SimplArchive.Api.CalDav;
using SimplArchive.Api.CalDav.Xml;

namespace SimplArchive.UnitTests;

// The DAV XML toolkit ported from SimplCalCon (ADR 0621). These are the pieces the hand-rolled middleware
// never had — it ignored the request body and always answered a fixed property set — covered here before the
// controllers that depend on them arrived. Since then the WebDAV gateway itself parses with PropRequest too
// (issue #801, ADR 0713), so this parser now answers the property question for every DAV surface.
public class DavToolkitTests
{
    [Fact]
    public void Propfind_with_no_body_is_allprop()
    {
        // RFC 4918 §9.1: an empty PROPFIND body means allprop.
        var request = PropRequest.Parse(null);
        Assert.True(request.AllProp);
        Assert.False(request.PropName);
    }

    [Fact]
    public void Propfind_parses_allprop_propname_and_an_explicit_list()
    {
        Assert.True(PropRequest.Parse(new XElement(DavNames.Propfind, new XElement(DavNames.AllProp))).AllProp);
        Assert.True(PropRequest.Parse(new XElement(DavNames.Propfind, new XElement(DavNames.PropName))).PropName);

        var explicitly = PropRequest.Parse(new XElement(DavNames.Propfind,
            new XElement(DavNames.Prop, new XElement(DavNames.GetEtag), new XElement(DavNames.ResourceType))));
        Assert.False(explicitly.AllProp);
        Assert.Equal([DavNames.GetEtag, DavNames.ResourceType], explicitly.Names);
    }

    [Fact]
    public void Multistatus_reports_a_requested_but_absent_property_as_404()
    {
        // The behaviour a client relies on to learn what a server does NOT have — the fixed-set middleware
        // could not express it at all.
        var resource = new DavResource("/caldav/calendars/x/").Set(DavNames.DisplayName, "Mine");
        var request = PropRequest.FromProp(new XElement(DavNames.Prop,
            new XElement(DavNames.DisplayName), new XElement(DavNames.SyncToken)));

        var document = MultiStatus.Build(request, [resource]);
        var propstats = document.Descendants(DavNames.Propstat).ToList();

        Assert.Contains(propstats, p => p.Element(DavNames.Status)!.Value.Contains("200")
            && p.Descendants(DavNames.DisplayName).Any());
        Assert.Contains(propstats, p => p.Element(DavNames.Status)!.Value.Contains("404")
            && p.Descendants(DavNames.SyncToken).Any());
    }

    [Fact]
    public void Proppatch_is_acknowledged_rather_than_refused()
    {
        // Apple's dataaccessd sets collection metadata during account setup and ABORTS when PROPPATCH 405s.
        // The pre-port middleware answered 405 — this is the single most consequential thing the port fixes,
        // and it is why Apple Calendar/Contacts could not have completed setup against it.
        var update = new XElement(DavNames.Dav + "propertyupdate",
            new XElement(DavNames.Dav + "set", new XElement(DavNames.Prop, new XElement(DavNames.DisplayName, "Renamed"))));

        var document = MultiStatus.PropPatchAccepted("/caldav/calendars/x/", update);

        Assert.Equal(DavNames.Multistatus, document.Root!.Name);
        Assert.Contains(document.Descendants(DavNames.Status), s => s.Value.Contains("200"));
        Assert.Contains(document.Descendants(DavNames.Prop).Elements(), e => e.Name == DavNames.DisplayName);
    }

    [Fact]
    public void Sync_tokens_round_trip_and_reject_a_foreign_one()
    {
        var token = DavTokens.Format(42);
        Assert.Equal(42, DavTokens.TryParse(token));
        Assert.Null(DavTokens.TryParse("https://example.invalid/other/42"));
        Assert.Null(DavTokens.TryParse(null));
        Assert.Null(DavTokens.TryParse(""));
    }
}
