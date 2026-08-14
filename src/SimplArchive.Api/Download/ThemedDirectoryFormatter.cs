using System.Net;
using System.Text;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace SimplArchive.Api.Download;

/// <summary>
/// Renders the <c>/download</c> listings in the product's own design instead of the framework's (ADR 0578).
/// </summary>
/// <remarks>
/// <para>
/// The listing itself is deliberate (ADR 0505): the web client resolves the visitor's operating system and
/// opens <c>/download/clients/&lt;os&gt;/</c>, and the archive filenames carry a version that changes every
/// release — so a hand-written page would go stale, where a directory listing cannot.
/// </para>
/// <para>
/// What was wrong is that it was <b>ASP.NET Core's</b> listing: "Index of /download/clients/windows/" in Segoe
/// UI, reached by clicking a button in a designed application. macOS visitors saw a proper page (its packages
/// live on a GitHub Release, ADR 0490) and everyone else saw a file index — the same product, two different
/// centuries, decided by which computer you own.
/// </para>
/// <para>
/// Sizes are shown because the difference between a 60 MB desktop client and a 2 MB manual is the one thing a
/// visitor wants before clicking on a metered connection, and the framework's formatter omitted it entirely.
/// </para>
/// </remarks>
public sealed class ThemedDirectoryFormatter : IDirectoryFormatter
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public async Task GenerateContentAsync(HttpContext context, IEnumerable<IFileInfo> contents)
    {
        context.Response.ContentType = "text/html; charset=utf-8";

        var path = context.Request.PathBase + context.Request.Path;
        var entries = contents.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase);

        var html = new StringBuilder();
        html.Append("""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <meta name="robots" content="noindex, nofollow">
              <title>SimplArchive — downloads</title>
              <link rel="stylesheet" href="/api/theme.css">
              <style>
                body { font-family: system-ui, -apple-system, "Segoe UI", Roboto, sans-serif;
                       max-width: 640px; margin: 3rem auto; padding: 0 1rem; line-height: 1.5;
                       background: var(--sa-canvas, #FAFAFC); color: var(--sa-text, #14161C); }
                h1 { font-size: 1.4rem; margin: 0 0 .25rem; }
                .path { color: var(--sa-text-secondary, #5A5F6E); font-size: .9rem; margin: 0 0 1.5rem; }
                ul { list-style: none; padding: 0; margin: 0; border-top: 1px solid var(--sa-hairline, #E6E7EC); }
                li { border-bottom: 1px solid var(--sa-hairline, #E6E7EC); }
                a.row { display: flex; justify-content: space-between; gap: 1rem; align-items: baseline;
                        padding: .7rem .25rem; text-decoration: none; color: inherit; }
                a.row:hover { background: var(--sa-accent-tint, #E4F3F1); }
                .name { overflow-wrap: anywhere; }
                .dir .name { font-weight: 600; color: var(--sa-accent-text, #0F766E); }
                .size { color: var(--sa-text-secondary, #5A5F6E); font-size: .85rem; white-space: nowrap;
                        font-variant-numeric: tabular-nums; }
                .empty { color: var(--sa-text-secondary, #5A5F6E); padding: 1rem .25rem; }
                footer { margin-top: 2rem; font-size: .85rem; color: var(--sa-text-faint, #898F9E); }
                footer a { color: inherit; }
              </style>
            </head>
            <body>
              <h1>Downloads</h1>

            """);

        html.Append("  <p class=\"path\">").Append(WebUtility.HtmlEncode(path)).Append("</p>\n  <ul>\n");

        // A parent link, so somebody who lands deep can get back out without editing the address bar.
        if (path.Value?.TrimEnd('/').Contains('/') == true && path != "/download")
        {
            html.Append("    <li class=\"dir\"><a class=\"row\" href=\"../\"><span class=\"name\">← up one level</span></a></li>\n");
        }

        var any = false;
        foreach (var entry in entries)
        {
            any = true;
            var name = WebUtility.HtmlEncode(entry.Name);
            var href = WebUtility.UrlEncode(entry.Name) + (entry.IsDirectory ? "/" : string.Empty);
            var size = entry.IsDirectory ? string.Empty : Humanise(entry.Length);

            html.Append("    <li").Append(entry.IsDirectory ? " class=\"dir\"" : string.Empty)
                .Append("><a class=\"row\" href=\"").Append(href).Append("\"><span class=\"name\">")
                .Append(name).Append(entry.IsDirectory ? "/" : string.Empty)
                .Append("</span><span class=\"size\">").Append(size).Append("</span></a></li>\n");
        }

        if (!any)
        {
            html.Append("    <li class=\"empty\">Nothing here yet.</li>\n");
        }

        html.Append("""
              </ul>
              <footer>
                <a href="/">← Back to SimplArchive</a>
              </footer>
            </body>
            </html>
            """);

        await context.Response.WriteAsync(html.ToString(), Encoding.UTF8);
    }

    // Decimal units, because that is what a download page beside a browser's own progress bar should agree with.
    private static string Humanise(long bytes) => bytes switch
    {
        >= 1_000_000_000 => $"{bytes / 1_000_000_000.0:0.0} GB",
        >= 1_000_000 => $"{bytes / 1_000_000.0:0.0} MB",
        >= 1_000 => $"{bytes / 1_000.0:0} kB",
        _ => $"{bytes} B",
    };
}
