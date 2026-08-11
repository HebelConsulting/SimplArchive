namespace SimplArchive.Client.Services;

/// <summary>Turns an advertised href into something a browser element can load directly.</summary>
public static class HttpClientUrlExtensions
{
    /// <summary>
    /// Resolves <paramref name="href"/> against the client's base address, leaving an already-absolute URL
    /// alone.
    /// </summary>
    /// <remarks>
    /// Needed because a rel's href is usually app-relative, while a preview or download URL is handed to the
    /// browser (an <c>&lt;img&gt;</c> src, a JS fetch, a new tab) which has no notion of the HttpClient's base.
    /// A presigned storage URL is already absolute and must pass through untouched.
    /// </remarks>
    public static string? Absolute(this HttpClient http, string? href) => href switch
    {
        null => null,
        var h when h.StartsWith("http://", StringComparison.Ordinal) || h.StartsWith("https://", StringComparison.Ordinal) => h,
        var h => new Uri(http.BaseAddress!, h).ToString(),
    };
}
