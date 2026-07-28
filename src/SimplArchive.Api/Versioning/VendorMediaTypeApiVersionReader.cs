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
    [GeneratedRegex(@"application/vnd\.simplarchive\.v(?<version>[\d.]+)\+(?:json|xml)", RegexOptions.IgnoreCase)]
    private static partial Regex VendorMediaTypePattern();

    public void AddParameters(IApiVersionParameterDescriptionContext context)
    {
        context.AddParameter("Accept", ApiVersionParameterLocation.Header);
    }

    public IReadOnlyList<string> Read(HttpRequest request)
    {
        var accept = request.Headers.Accept.ToString();
        var match = VendorMediaTypePattern().Match(accept);

        return match.Success ? [match.Groups["version"].Value] : [];
    }
}
