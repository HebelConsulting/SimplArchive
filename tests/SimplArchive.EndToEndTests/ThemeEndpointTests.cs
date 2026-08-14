using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace SimplArchive.EndToEndTests;

/// <summary>
/// The theme endpoints, at the exact URLs the hand-written pages link (ADR 0578).
/// </summary>
/// <remarks>
/// <para>
/// <b>These exist because the CSS route shipped as a 404 and reached a browser.</b> The controller carried
/// <c>[Route("api/theme")]</c> and the action <c>[HttpGet("theme.css")]</c>, which composes to
/// <c>/api/theme/theme.css</c> — while five pages linked <c>/api/theme.css</c>. Every unit test still passed,
/// because none of them asked the server for a URL.
/// </para>
/// <para>
/// The damage was disproportionate to the typo: with the sheet missing, <c>var(--sa-accent)</c> resolves to
/// <em>nothing</em>, so <c>background: var(--sa-accent)</c> made the sign-in button <b>invisible</b>. A colour
/// that fails to load should degrade to the wrong colour, never to no control — hence the fallbacks now in
/// those pages, and hence this test, which asserts the address rather than the behaviour behind it.
/// </para>
/// <para>
/// Anonymous on purpose: the sign-in page is rendered before anyone has a token, so a theme that needed one
/// would arrive exactly one page too late.
/// </para>
/// </remarks>
[Collection(E2ECollection.Name)]
public class ThemeEndpointTests
{
    private readonly E2EApiFactory _factory;

    public ThemeEndpointTests(E2EApiFactory factory) => _factory = factory;

    [Theory]
    [InlineData("/api/theme")]
    [InlineData("/api/theme.css")]
    public async Task Both_theme_endpoints_answer_without_a_token(string url)
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(url);

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"GET {url} returned {(int)response.StatusCode}. Five hand-written pages link this exact address as "
            + "a stylesheet; a 404 leaves every --sa-* variable unresolved and the sign-in button invisible.");
    }

    /// <summary>The sheet is CSS, and carries the variables the pages actually name.</summary>
    [Fact]
    public async Task The_stylesheet_defines_the_variables_the_pages_use()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/api/theme.css");
        var css = await response.Content.ReadAsStringAsync();

        Assert.Equal("text/css", response.Content.Headers.ContentType?.MediaType);

        // The ones the sign-in page, the passkey page and the external-link landing page bind to.
        foreach (var variable in new[]
                 {
                     "--sa-accent:", "--sa-accent-hover:", "--sa-on-accent:", "--sa-danger:",
                     "--sa-canvas:", "--sa-surface:", "--sa-text:", "--sa-text-secondary:", "--sa-hairline:",
                 })
        {
            Assert.Contains(variable, css);
        }

        // Both themes, so a page that only links this sheet still follows the operating system.
        Assert.Contains("prefers-color-scheme: dark", css);
    }

    /// <summary>
    /// The external-link landing page renders no stray template syntax — its HTML is a raw string in a C#
    /// file, so anything Razor-shaped written there is <b>text</b>.
    /// </summary>
    /// <remarks>
    /// An <c>@* … *@</c> comment written into that string rendered as a paragraph of the developer's prose in
    /// the middle of the shared-document card, and pushed the layout sideways. It reached a browser because
    /// the tests above assert the page's ADDRESS and its stylesheet — and a page can be perfectly well
    /// addressed while showing a stranger somebody's notes.
    /// </remarks>
    [Fact]
    public async Task The_landing_page_contains_no_unrendered_template_syntax()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Accept", "text/html");

        // Any token resolves to a page: an unknown one renders the "no longer available" variant, built from
        // the same string, which would leak the same way.
        using var response = await client.GetAsync("/api/external-links/not-a-real-token");
        var html = await response.Content.ReadAsStringAsync();

        foreach (var leak in new[] { "@*", "*@", "@{", "@Html" })
        {
            Assert.False(
                html.Contains(leak, StringComparison.Ordinal),
                $"The landing page contains '{leak}'. Its HTML is a raw string in a C# file, not Razor, so "
                + "template syntax written there is rendered to the visitor as text.");
        }
    }

    /// <summary>
    /// The JSON form is what the web client applies, and it must be reachable by following the API root's
    /// <c>theme</c> rel rather than by composing a URL (ADR 0543) — anonymously, because that is the point.
    /// </summary>
    [Fact]
    public async Task The_api_root_advertises_the_theme_rel_anonymously()
    {
        using var client = _factory.CreateClient();

        var root = await client.GetFromJsonAsync<JsonElement>("/api");
        var href = root.GetProperty("links").EnumerateArray()
            .FirstOrDefault(l => l.GetProperty("rel").GetString() == "theme")
            .GetProperty("href").GetString();

        Assert.False(string.IsNullOrEmpty(href), "The API root does not advertise a 'theme' rel.");

        var tokens = await client.GetFromJsonAsync<JsonElement>(href!);
        Assert.False(string.IsNullOrWhiteSpace(tokens.GetProperty("light").GetProperty("accent")
            .GetProperty("primary").GetString()));
    }

    /// <summary>
    /// A file dropped into <c>custom/</c> replaces the browser-tab icon, and removing it restores the shipped one.
    /// </summary>
    /// <remarks>
    /// The favicon is the one brand surface a runtime theme cannot reach: it is a committed artefact generated
    /// from the tokens, and re-rendering it per request would need a rasteriser the Alpine image does not have.
    /// So it is a drop-in beside the theme — and the ORDER is what makes it work, since static files serve the
    /// first match and the shipped icon is already in wwwroot. A test that only checked "a favicon is served"
    /// would pass with the override doing nothing at all.
    /// </remarks>
    [Fact]
    public async Task A_custom_favicon_overrides_the_shipped_one()
    {
        using var client = _factory.CreateClient();
        var environment = _factory.Services.GetRequiredService<IWebHostEnvironment>();
        var directory = Path.Combine(environment.ContentRootPath, "custom");
        var file = Path.Combine(directory, "favicon.ico");

        var shipped = await (await client.GetAsync("/favicon.ico")).Content.ReadAsByteArrayAsync();
        Assert.NotEmpty(shipped);

        Directory.CreateDirectory(directory);
        var ours = new byte[] { 0, 0, 1, 0, 4, 2, 4, 2 };
        await File.WriteAllBytesAsync(file, ours);
        try
        {
            var served = await (await client.GetAsync("/favicon.ico")).Content.ReadAsByteArrayAsync();

            Assert.Equal(ours, served);
            Assert.NotEqual(shipped, served);
        }
        finally
        {
            File.Delete(file);
        }

        // And the shipped icon comes back, so an operator can undo it by deleting a file.
        Assert.Equal(shipped, await (await client.GetAsync("/favicon.ico")).Content.ReadAsByteArrayAsync());
    }
}
