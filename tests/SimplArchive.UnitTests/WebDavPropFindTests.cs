using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SimplArchive.Api.CalDav.Xml;
using SimplArchive.Api.WebDav;

namespace SimplArchive.UnitTests;

// PROPFIND answers the question it was asked (issue #801, RFC 4918 §9.1): the requested-and-known properties
// in the 200 propstat, the requested-and-unknown ones in a 404 propstat NAMING them — instead of the fixed
// property set the gateway used to emit no matter what the body said. The parser is the ported PropRequest
// (ADR 0621, covered by DavToolkitTests); what is covered HERE is the partitioning of the gateway's composed
// propstat fragments against it, which is applied at the one place every WebDAV multistatus passes through.
public class WebDavPropFindTests
{
    private static readonly XNamespace Dav = "DAV:";

    // A realistic FileProp-shaped fragment: named values plus one property whose VALUE has child elements.
    private const string FileProps =
        "<D:displayname>report.docx</D:displayname><D:resourcetype/>" +
        "<D:getcontentlength>42</D:getcontentlength>" +
        "<D:getetag>\"42-abc\"</D:getetag>" +
        "<D:supportedlock><D:lockentry><D:lockscope><D:exclusive/></D:lockscope></D:lockentry></D:supportedlock>";

    private static PropStatXml Response(string props = FileProps) => new("/SimplArchive/x", "HTTP/1.1 200 OK", props);

    private static PropRequest Request(params XName[] names) =>
        PropRequest.FromProp(new XElement(DavNames.Prop, names.Select(n => new XElement(n))));

    [Fact]
    public void An_explicit_list_is_partitioned_into_a_200_and_a_404_propstat()
    {
        var propstats = WebDavPropFind.Apply(Request(DavNames.GetEtag, Dav + "quota-available-bytes"), Response());

        Assert.Equal(2, propstats.Count);

        var ok = Parse(propstats[0].Props);
        Assert.Equal("HTTP/1.1 200 OK", propstats[0].Status);
        Assert.Equal("\"42-abc\"", ok.Element(Dav + "getetag")?.Value);
        Assert.Null(ok.Element(Dav + "displayname")); // present in the composed set, NOT requested — stays out

        var notFound = Parse(propstats[1].Props);
        Assert.Equal("HTTP/1.1 404 Not Found", propstats[1].Status);
        Assert.NotNull(notFound.Element(Dav + "quota-available-bytes"));
        Assert.Empty(notFound.Element(Dav + "quota-available-bytes")!.Elements());
    }

    [Fact]
    public void A_request_naming_only_unknown_properties_yields_only_the_404_propstat()
    {
        var propstats = WebDavPropFind.Apply(Request(Dav + "quota-available-bytes"), Response());

        var propstat = Assert.Single(propstats);
        Assert.Equal("HTTP/1.1 404 Not Found", propstat.Status);
        Assert.NotNull(Parse(propstat.Props).Element(Dav + "quota-available-bytes"));
    }

    [Fact]
    public void A_property_whose_value_has_children_survives_selection_intact()
    {
        var propstats = WebDavPropFind.Apply(Request(DavNames.Dav + "supportedlock"), Response());

        var ok = Parse(Assert.Single(propstats).Props);
        Assert.NotNull(ok.Element(Dav + "supportedlock")?.Element(Dav + "lockentry"));
    }

    [Fact]
    public void A_request_in_a_foreign_namespace_is_named_back_in_that_namespace()
    {
        XNamespace win = "urn:schemas-microsoft-com:";
        var propstats = WebDavPropFind.Apply(Request(win + "Win32FileAttributes"), Response());

        var propstat = Assert.Single(propstats);
        Assert.Equal("HTTP/1.1 404 Not Found", propstat.Status);
        Assert.NotNull(Parse(propstat.Props).Element(win + "Win32FileAttributes"));
    }

    [Fact]
    public void Propname_reports_the_names_and_strips_every_value()
    {
        var request = PropRequest.Parse(new XElement(DavNames.Propfind, new XElement(DavNames.PropName)));

        var propstat = Assert.Single(WebDavPropFind.Apply(request, Response()));
        var prop = Parse(propstat.Props);

        Assert.NotNull(prop.Element(Dav + "getetag"));
        Assert.All(prop.Elements(), e => Assert.True(e.IsEmpty || !e.Nodes().Any(), $"{e.Name.LocalName} kept its value"));
    }

    [Fact]
    public void Allprop_passes_the_composed_set_through_byte_identically()
    {
        var propstat = Assert.Single(WebDavPropFind.Apply(PropRequest.Parse(null), Response()));
        Assert.Equal(FileProps, propstat.Props);
    }

    [Theory]
    [InlineData("")]                                 // no body ⇒ allprop (RFC 4918 §9.1)
    [InlineData("this is not xml <")]                // unparsable ⇒ allprop, with a Warning naming it (ADR 0626)
    [InlineData("<propfind><prop/></propfind>")]     // no DAV: namespace ⇒ nothing recognisable ⇒ allprop
    public async Task A_body_that_names_no_property_set_degrades_to_allprop(string body)
    {
        var request = await WebDavPropFind.ReadRequestAsync(ContextWith(body));
        Assert.True(request.AllProp);
    }

    [Fact]
    public async Task A_dav_prop_list_is_read_from_the_body()
    {
        var request = await WebDavPropFind.ReadRequestAsync(ContextWith(
            "<?xml version=\"1.0\"?><D:propfind xmlns:D=\"DAV:\"><D:prop><D:getetag/><D:getlastmodified/></D:prop></D:propfind>"));

        Assert.False(request.AllProp);
        Assert.Equal([DavNames.GetEtag, DavNames.Dav + "getlastmodified"], request.Names);
    }

    private static XElement Parse(string props) => XElement.Parse($"<D:prop xmlns:D=\"DAV:\">{props}</D:prop>");

    private static DefaultHttpContext ContextWith(string body) => new()
    {
        Request = { Body = new MemoryStream(Encoding.UTF8.GetBytes(body)) },
        RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
    };
}
