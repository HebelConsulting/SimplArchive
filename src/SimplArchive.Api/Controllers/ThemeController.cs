using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SimplArchive.Theming;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// The colours this installation wears (ADR 0578) — the shipped design, or whatever the operator put in
/// <c>custom/theme.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// One identity per <b>installation</b>, not per tenant. That is the on-premises reality — a company installs
/// SimplArchive and wants their colours — and it is the only scope that can theme the login page, which is
/// rendered before any tenant is known.
/// </para>
/// <para>
/// <b>Anonymous, and it has to be.</b> The web client applies the theme before anybody has signed in; a login
/// page that flashes the shipped design and then repaints is worse than one that never had a brand. There is
/// nothing here to protect: a colour is not a secret, and the response is the same for every caller.
/// </para>
/// <para>
/// The desktop client does not use this — it picks a bundled style per server profile instead, because a
/// desktop user chooses their own and because it must have colours before it has a server. Both paths read the
/// same tokens through the same loader, so neither can drift into its own idea of what the design is.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/theme")]
[AllowAnonymous]
public class ThemeController(IWebHostEnvironment environment, ILogger<ThemeController> logger) : ControllerBase
{
    /// <summary>Where an operator drops a custom theme — a mounted volume, in every real deployment.</summary>
    public const string CustomThemePath = "custom/theme.json";

    /// <summary>The effective palette: light and dark, accent, semantics and neutrals.</summary>
    [HttpGet]
    public ActionResult<ThemeTokens> Get() => Ok(Effective());

    // Standing convention: every GET action gets a companion HEAD action.
    [HttpHead]
    public IActionResult Head() => NoContent();

    /// <summary>
    /// The same palette as a CSS custom-property sheet, so the browser can have it without a round trip through
    /// JavaScript and a repaint.
    /// </summary>
    /// <remarks>
    /// Generated per request rather than cached: it is a few hundred bytes of string building, and an operator
    /// who edits <c>custom/theme.json</c> expects a reload to show it rather than a restart. The response is
    /// deliberately not cached downstream for the same reason.
    /// </remarks>
    // An ABSOLUTE route (leading slash), so this is /api/theme.css and not /api/theme/theme.css. It is linked
    // from five hand-written pages as a stylesheet, and a stylesheet's URL wants to look like a file.
    [HttpGet("/api/theme.css")]
    [Produces("text/css")]
    public IActionResult Css()
    {
        Response.Headers.CacheControl = "no-cache";
        return Content(ThemeEmitter.ToCss(Effective()), "text/css");
    }

    [HttpHead("/api/theme.css")]
    public IActionResult CssHead() => NoContent();

    // Read on every request. A theme file is read once per page load in practice, and holding it in memory
    // would mean an operator's edit needs a restart to show — the surprise being that nothing appears to
    // happen, which is the worst kind.
    private ThemeTokens Effective()
    {
        var path = Path.Combine(environment.ContentRootPath, CustomThemePath);
        if (!System.IO.File.Exists(path))
        {
            return ThemeTokensReader.Shipped;
        }

        string json;
        try
        {
            json = System.IO.File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(e, "The custom theme at {Path} could not be read; the shipped design is in use.", path);
            return ThemeTokensReader.Shipped;
        }

        var load = ThemeTokensReader.Load(json);
        foreach (var note in load.Notes)
        {
            // Warning, not Information: every note here is something an operator has to change — a colour that
            // fails contrast, a section that was ignored, a value that is not a colour. The theme they meant to
            // apply is not the one on screen.
            logger.LogWarning("Custom theme: {Note}", note);
        }

        return load.Tokens;
    }
}
