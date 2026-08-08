using System.Globalization;
using System.Text.Encodings.Web;
using SimplArchive.Localization;

namespace SimplArchive.Api.Documents;

/// <summary>
/// The HTML a browser gets when someone opens an external link (ADR 0546).
/// </summary>
/// <remarks>
/// <para>
/// An external link is given to a person with <b>no account and no client</b> — all they have is a browser, so the
/// redemption endpoint answering with a JSON resource meant they were shown a wall of machine-readable text with a
/// URL buried in it. This page is what "the link opens the document" actually requires.
/// </para>
/// <para>
/// Deliberately hand-written HTML rather than a Razor Page or anything from the SPA: this is the only surface in
/// the system rendered for someone outside it, and it must not depend on the Blazor bundle, a stylesheet, a font
/// or any other request that could fail, be blocked, or leak what else exists here. One self-contained document,
/// no external references.
/// </para>
/// <para>
/// It is localized from <c>Accept-Language</c> like the login page (the recipient has no in-app language setting
/// to consult), and the document's name is HTML-encoded — it is tenant-authored text on an anonymous page, which
/// is precisely the shape of an injected-script problem.
/// </para>
/// </remarks>
public static class ExternalLinkPage
{
    /// <summary>The page for a live link: what it is, how long it lasts, and the two ways to take it.</summary>
    /// <param name="fileName">The document's file name, as the recipient should see it.</param>
    /// <param name="expiresAt">When the share stops working, in the recipient's own words.</param>
    /// <param name="contentPath">
    /// The route that mints a fresh presigned URL and redirects to it. The page holds no storage URL of its own:
    /// a presigned URL lives two minutes, so one baked into the page would be dead by the time a person had read
    /// the page and decided — handing them the storage provider's raw XML error, which is exactly the ugliness
    /// this page exists to remove.
    /// </param>
    public static string Live(string fileName, DateTimeOffset expiresAt, string contentPath)
    {
        var encoded = HtmlEncoder.Default.Encode(fileName);
        var until = string.Format(
            Strings.Get("ExtLinkPageAvailableUntil"), expiresAt.ToLocalTime().ToString("d MMMM yyyy"));

        return Document(
            Strings.Get("ExtLinkPageTitle"),
            $"""
                 <h1>{encoded}</h1>
                 <p class="meta">{HtmlEncoder.Default.Encode(until)}</p>
                 <p class="actions">
                   <a class="btn primary" href="{HtmlEncoder.Default.Encode(contentPath)}">{Strings.Get("ExtLinkPageOpen")}</a>
                   <a class="btn" href="{HtmlEncoder.Default.Encode(contentPath)}?download=true">{Strings.Get("ExtLinkPageDownload")}</a>
                 </p>
             """);
    }

    /// <summary>
    /// The page for a link that is not usable. Says the same thing for every cause — expired, exhausted, revoked,
    /// unknown, tenant switched off — because distinguishing them would tell a stranger which tokens exist, which
    /// is the one thing this endpoint must never do (ADR 0546).
    /// </summary>
    public static string Gone() =>
        Document(
            Strings.Get("ExtLinkPageGoneTitle"),
            $"""
                 <h1>{Strings.Get("ExtLinkPageGoneTitle")}</h1>
                 <p class="meta">{Strings.Get("ExtLinkPageGoneBody")}</p>
             """);

    // $$""" so the CSS keeps its own braces: with two $ the interpolation delimiter becomes {{ }}, which leaves
    // every { in the stylesheet as itself instead of a doubled escape nobody can read.
    // "Shared via {0}" with the product name as the link. The TEMPLATE is encoded and the anchor spliced in
    // afterwards, so a translator's text can never inject markup while the one bit of HTML here stays ours —
    // and {0} keeps the word order each language actually wants.
    private static string FooterLine() =>
        string.Format(
            HtmlEncoder.Default.Encode(Strings.Get("ExtLinkPageFooter")),
            "<a href=\"https://www.simplarchive.dev\">SimplArchive</a>");

    private static string Document(string title, string body) =>
        $$"""
        <!DOCTYPE html>
        <html lang="{{HtmlEncoder.Default.Encode(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName)}}">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <!-- A shared document is not something to index, nor to name in a referrer header on the way out. -->
          <meta name="robots" content="noindex, nofollow">
          <meta name="referrer" content="no-referrer">
          <title>{{HtmlEncoder.Default.Encode(title)}}</title>
          <style>
            :root { color-scheme: light dark; }
            body { margin: 0; min-height: 100vh; display: flex; align-items: center; justify-content: center;
                   font-family: system-ui, -apple-system, "Segoe UI", Roboto, sans-serif;
                   background: #f4f4f7; color: #1c1c21; }
            main { max-width: 32rem; padding: 2.5rem; margin: 1rem; background: #fff; border-radius: 12px;
                   box-shadow: 0 1px 3px rgba(0,0,0,.12), 0 8px 24px rgba(0,0,0,.08); text-align: center; }
            h1 { font-size: 1.35rem; line-height: 1.35; margin: 0 0 .5rem; overflow-wrap: anywhere; }
            .meta { color: #5b5b66; font-size: .95rem; margin: 0 0 1.75rem; }
            .actions { display: flex; gap: .75rem; justify-content: center; flex-wrap: wrap; margin: 0; }
            .btn { display: inline-block; padding: .6rem 1.25rem; border-radius: 8px; text-decoration: none;
                   border: 1px solid #d3d3dc; color: #1c1c21; font-size: .95rem; }
            .btn.primary { background: #5b4ee5; border-color: #5b4ee5; color: #fff; }
            footer { margin-top: 2rem; font-size: .8rem; color: #8a8a96; }
            footer a { color: inherit; }
            /* Smaller and quieter than the line above it: the address must not compete with the document name
               and the two buttons, which are what the recipient came for (issue #411). */
            address { margin-top: .75rem; font-style: normal; font-size: .72rem; line-height: 1.5; color: #9a9aa4; }
            @media (prefers-color-scheme: dark) {
              body { background: #16161a; color: #ececf1; }
              main { background: #1f1f25; box-shadow: none; }
              .meta { color: #a4a4b0; }
              .btn { border-color: #3a3a44; color: #ececf1; }
              .btn.primary { background: #6f63ff; border-color: #6f63ff; color: #fff; }
            }
          </style>
        </head>
        <body>
          <main>
        {{body}}
            <footer>
              <!-- The product name links out (issue #411): this page is the one surface in the system seen by
                   people with NO account, and a recipient who has never heard of SimplArchive otherwise has no
                   way to find out what it is. Safe to link because the page sets referrer: no-referrer, so the
                   token-bearing URL is not handed to the site it points at. -->
              {{FooterLine()}}
              <!-- Deliberately NOT localised: a postal address does not translate. -->
              <address>
                &copy; 2026<br>
                Hebel Consulting GmbH<br>
                Schweighofplatz 7<br>
                6010 Kriens<br>
                Switzerland<br>
                <a href="mailto:support@simplarchive.dev">support@simplarchive.dev</a>
              </address>
            </footer>
          </main>
        </body>
        </html>
        """;
}
