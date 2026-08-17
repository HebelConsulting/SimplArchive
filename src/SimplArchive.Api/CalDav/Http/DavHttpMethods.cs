// PORTED from the sister project SimplCalCon (Apache-2.0, same licence), whose DAV layer is proven against
// DAVx⁵ and Apple's clients — see ADR 0621. Kept close to the original so a fix there is easy to carry over;
// only the namespace and the storage-facing types are adapted.
using Microsoft.AspNetCore.Mvc.Routing;

namespace SimplArchive.Api.CalDav.Http;

/// <summary>Routes the WebDAV <c>PROPFIND</c> method (RFC 4918).</summary>
public sealed class HttpPropfindAttribute : HttpMethodAttribute
{
    private static readonly string[] Supported = ["PROPFIND"];

    public HttpPropfindAttribute()
        : base(Supported)
    {
    }

    public HttpPropfindAttribute(string template)
        : base(Supported, template)
    {
    }
}

/// <summary>Routes the WebDAV <c>PROPPATCH</c> method (RFC 4918).</summary>
public sealed class HttpProppatchAttribute : HttpMethodAttribute
{
    private static readonly string[] Supported = ["PROPPATCH"];

    public HttpProppatchAttribute()
        : base(Supported)
    {
    }

    public HttpProppatchAttribute(string template)
        : base(Supported, template)
    {
    }
}

/// <summary>Routes the WebDAV <c>REPORT</c> method (RFC 3253/6578).</summary>
public sealed class HttpReportAttribute : HttpMethodAttribute
{
    private static readonly string[] Supported = ["REPORT"];

    public HttpReportAttribute()
        : base(Supported)
    {
    }

    public HttpReportAttribute(string template)
        : base(Supported, template)
    {
    }
}

/// <summary>Routes the WebDAV <c>MKCOL</c> method (extended MKCOL, RFC 5689).</summary>
public sealed class HttpMkcolAttribute : HttpMethodAttribute
{
    private static readonly string[] Supported = ["MKCOL"];

    public HttpMkcolAttribute()
        : base(Supported)
    {
    }

    public HttpMkcolAttribute(string template)
        : base(Supported, template)
    {
    }
}

/// <summary>Routes the CalDAV <c>MKCALENDAR</c> method (RFC 4791).</summary>
public sealed class HttpMkcalendarAttribute : HttpMethodAttribute
{
    private static readonly string[] Supported = ["MKCALENDAR"];

    public HttpMkcalendarAttribute()
        : base(Supported)
    {
    }

    public HttpMkcalendarAttribute(string template)
        : base(Supported, template)
    {
    }
}
