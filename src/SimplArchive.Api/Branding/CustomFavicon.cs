namespace SimplArchive.Api.Branding;

/// <summary>
/// Lets an installation replace the browser-tab icon by dropping a file into <c>custom/</c> (ADR 0578).
/// </summary>
/// <remarks>
/// <para>
/// The shipped favicon is a committed artefact, generated from the design tokens by
/// <c>scripts/generate-icons.sh</c> — so it follows the shipped accent and <b>cannot</b> follow a customer's
/// <c>custom/theme.json</c>, which is applied at runtime. Re-rendering it per request would need a rasteriser,
/// and the Api image is Alpine precisely because it has none.
/// </para>
/// <para>
/// So the tab icon is a file, beside the theme it belongs with: <c>custom/favicon.ico</c> or
/// <c>custom/favicon.png</c>. An operator who has already mounted <c>custom/</c> for their colours adds one
/// more file and the identity is complete — app, sign-in page, external-link landing page and tab.
/// </para>
/// <para>
/// Ordered <b>before</b> the static-file middleware, because that is the only way to win: static files serve
/// the first match, and the shipped icon is already in wwwroot.
/// </para>
/// </remarks>
public static class CustomFavicon
{
    private static readonly (string Request, string File, string ContentType)[] Candidates =
    [
        ("/favicon.ico", "favicon.ico", "image/x-icon"),
        ("/favicon.png", "favicon.png", "image/png"),
    ];

    public static IApplicationBuilder UseCustomFavicon(this WebApplication app)
    {
        var directory = Path.Combine(app.Environment.ContentRootPath, "custom");

        return app.Use(async (context, next) =>
        {
            var match = Candidates.FirstOrDefault(c =>
                string.Equals(context.Request.Path.Value, c.Request, StringComparison.OrdinalIgnoreCase));

            var path = match.File is null ? null : Path.Combine(directory, match.File);
            if (path is null || !File.Exists(path))
            {
                await next();
                return;
            }

            // Short rather than none: browsers cache a favicon aggressively and an operator who replaces the
            // file expects to see it, but re-reading it on every page load of every visitor would be silly.
            context.Response.Headers.CacheControl = "public, max-age=300";
            context.Response.ContentType = match.ContentType;

            try
            {
                await context.Response.SendFileAsync(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // An unreadable override costs the custom icon, never the page. Nothing has been written to the
                // body yet, so the shipped one can still be served.
                app.Logger.LogWarning(e, "The custom favicon at {Path} could not be read.", path);
                context.Response.Clear();
                await next();
            }
        });
    }
}
