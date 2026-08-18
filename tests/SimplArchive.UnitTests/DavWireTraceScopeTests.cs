using Microsoft.AspNetCore.Http;
using SimplArchive.Api.CalDav;

namespace SimplArchive.UnitTests;

// What the DAV wire trace covers, and what it refuses to capture (#595, ADR 0626).
//
// Both questions are decided by two small predicates, and both were wrong before: the WebDAV gateway's file
// operations were not considered DAV at all — so GET/PUT/DELETE/MOVE were invisible even with the category at
// Verbose, and the unhandled-request Warning never fired for them, which is exactly where "my file did not
// upload" lives. Fixing the coverage then created the opposite hazard, because the verbose path reads the whole
// request into a string and buffers the whole response.
//
// Unit-testable on purpose: the alternative is an end-to-end test that has to raise a log level process-wide and
// then assert on log output, which is slower, flakier, and tests less.
public class DavWireTraceScopeTests
{
    private static HttpRequest Request(string method, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        return context.Request;
    }

    [Theory]
    // The two protocol roots, which were always covered.
    [InlineData("PROPFIND", "/caldav/calendars/")]
    [InlineData("REPORT", "/carddav/addressbooks/x")]
    // Discovery, which a client probes before it has credentials.
    [InlineData("PROPFIND", "/.well-known/caldav")]
    // The WebDAV gateway — the gap. Every one of these was previously invisible.
    [InlineData("GET", "/SimplArchive/Personal/My Documents/report.pdf")]
    [InlineData("PUT", "/SimplArchive/Personal/report.pdf")]
    [InlineData("DELETE", "/SimplArchive/Personal/report.pdf")]
    [InlineData("MOVE", "/SimplArchive/Personal/report.pdf")]
    [InlineData("LOCK", "/SimplArchive/Personal/report.pdf")]
    [InlineData("OPTIONS", "/SimplArchive")]
    // The legacy mount path still answers, so it must still be traced.
    [InlineData("GET", "/webdav/Personal/report.pdf")]
    public void The_trace_covers_every_dav_surface(string method, string path) =>
        Assert.True(DavWireTraceMiddleware.IsDav(Request(method, path)),
            $"{method} {path} is served by a DAV surface but would not be traced");

    [Theory]
    [InlineData("GET", "/api/documents")]
    [InlineData("POST", "/connect/token")]
    [InlineData("GET", "/health/ready")]
    public void It_does_not_cover_the_ordinary_api(string method, string path) =>
        Assert.False(DavWireTraceMiddleware.IsDav(Request(method, path)),
            $"{method} {path} is not DAV and must not be buffered or logged by the DAV trace");

    // The asymmetry is the decision, so it is asserted in both directions rather than described in a comment.
    [Theory]
    // A file transfer on the gateway: summarised, never captured — it is a user's document, and buffering it
    // would hold a large download in memory before its first byte reached the client.
    [InlineData("GET", "/SimplArchive/Personal/scan.pdf", true)]
    [InlineData("HEAD", "/SimplArchive/Personal/scan.pdf", true)]
    [InlineData("PUT", "/SimplArchive/Personal/scan.pdf", true)]
    [InlineData("GET", "/webdav/Personal/scan.pdf", true)]
    // Protocol on the same gateway: captured, because the body is XML and that is the diagnosis.
    [InlineData("PROPFIND", "/SimplArchive/Personal", false)]
    [InlineData("PROPPATCH", "/SimplArchive/Personal", false)]
    [InlineData("MOVE", "/SimplArchive/Personal/scan.pdf", false)]
    [InlineData("DELETE", "/SimplArchive/Personal/scan.pdf", false)]
    // CalDAV/CardDAV item bodies stay captured even for PUT/GET: they are small text items, and seeing the exact
    // vCard a client sent is usually the whole of the interop question. The line is what the payload IS, not
    // which verb carried it.
    [InlineData("PUT", "/carddav/addressbooks/abc/ada.vcf", false)]
    [InlineData("GET", "/caldav/calendars/abc/event.ics", false)]
    public void Only_gateway_file_transfers_are_withheld_from_the_trace(string method, string path, bool withheld) =>
        Assert.Equal(withheld, DavWireTraceMiddleware.CarriesFileContent(Request(method, path)));
}
