using System.Text.RegularExpressions;
using Asp.Versioning;

namespace SimplArchive.Api.Versioning;

// Parses ADR "API versioning and error response model"'s exact format — the version lives in the
// media-type subtype (application/vnd.simplarchive.v1+json), not a separate query/header parameter, so
// none of Asp.Versioning's built-in readers match it. See ADR "Media-type/Accept-header API versioning
// (foundation slice)". Matches either +json or +xml (ADR "JSON/XML content negotiation") — version
// resolution is independent of which format was requested.
public partial class VendorMediaTypeApiVersionReader : IApiVersionReader
{
    // The format suffix is deliberately NOT required to read the version. Version resolution is independent of
    // which format was requested (the file header says so), and requiring "+json"/"+xml" here made that untrue in
    // the one case that matters: an Accept naming OUR media type in a shape the pattern did not cover — say
    // "application/vnd.simplarchive.v2" or "…v2+hal+json" — read as NO version requested, and
    // AssumeDefaultVersionWhenUnspecified then served v1 silently. The client asked for v2, received v1, and was
    // never told (#595, ADR 0626). Extracting the version regardless of suffix lets the versioning layer answer
    // properly with UNSUPPORTED_API_VERSION.
    [GeneratedRegex(@"application/vnd\.simplarchive\.v(?<version>[\d.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex VendorMediaTypePattern();

    /// <summary>Our media type named without a readable version — a request we must not silently reinterpret.</summary>
    [GeneratedRegex(@"application/vnd\.simplarchive\b", RegexOptions.IgnoreCase)]
    private static partial Regex VendorPrefixPattern();

    public void AddParameters(IApiVersionParameterDescriptionContext context)
    {
        context.AddParameter("Accept", ApiVersionParameterLocation.Header);
    }

    public IReadOnlyList<string> Read(HttpRequest request)
    {
        var accept = request.Headers.Accept.ToString();
        var match = VendorMediaTypePattern().Match(accept);
        if (match.Success)
        {
            return [match.Groups["version"].Value];
        }

        // No version, and the caller never named our media type: the documented default path — a plain
        // "application/json" (or a browser's "*/*") gets the current version. Silence is correct here; warning
        // would fire on every ordinary request.
        if (!VendorPrefixPattern().IsMatch(accept))
        {
            return [];
        }

        // But our media type named in a shape we cannot read is a different thing: the caller was being
        // explicit and we are about to serve them something else. Say so (ADR 0626) — the request still
        // proceeds on the default version, because refusing an Accept header we merely failed to parse would
        // be a worse answer than serving the only version we have.
        request.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("SimplArchive.Api.Versioning")
            .LogWarning(
                "Unreadable SimplArchive media type in Accept: {Accept} — serving the DEFAULT version, which is "
                + "probably not what the caller asked for. Expected application/vnd.simplarchive.v<version>+json "
                + "or +xml. Set Serilog:MinimumLevel:Override:SimplArchive.Api.Versioning to Trace for the "
                + "request detail.",
                accept);

        return [];
    }
}
