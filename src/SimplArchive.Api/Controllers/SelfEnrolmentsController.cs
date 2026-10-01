using Asp.Versioning;
using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// What a person may enrol for themselves, declared by the modules this tenant has (ADR 0864, #1502).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this lives under <c>me</c> and not under <c>modules</c>.</b> ADR 0864 named the per-module resource
/// <c>GET /api/modules/{id}/self-enrolment</c> and left discovery open — and <c>GET /api/modules</c> is
/// <b>tenant-administrator only</b>, since it carries licence state, activation and settings rels. So a rel
/// there would be invisible to exactly the people this surface exists for: ordinary users enrolling their own
/// credential. Owner-decided 2026-10-01.
/// </para>
/// <para>
/// <b>One read, and no module id known in advance</b> (ADR 0557). A client asks <c>me</c> for the
/// <c>selfEnrolments</c> rel, reads this collection once, and has every declaration with its gate already
/// evaluated and the address to POST to. The alternative — a rel per module, named after the module — makes
/// the rel NAME carry data, which is a composed address wearing a rel's clothes, and still costs a second
/// read to learn whether the surface is open.
/// </para>
/// <para>
/// <b>Three states, deliberately distinct</b>, because only one of them is something anybody can change:
/// a module that declares nothing is simply absent from the list; a declaration whose gate is off is present
/// with <c>enabled: false</c>, which an administrator can switch on; and a module that is not active at all
/// is absent, because its behaviour is absent (ADR 0740).
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/me/self-enrolments")]
[Authorize]
public class SelfEnrolmentsController(
    SimplArchiveDbContext dbContext,
    IReadOnlyList<ModuleLoader.LoadedModule> modules) : ControllerBase
{
    public class SelfEnrolmentsResource : HypermediaResource
    {
        public List<SelfEnrolmentResource> Items { get; set; } = [];
    }

    public class SelfEnrolmentResource : HypermediaResource
    {
        public string ModuleId { get; set; } = string.Empty;

        /// <summary>What is being enrolled, in the request's language — the module's own wording.</summary>
        /// <remarks>
        /// Resolved from the module's catalog (ABI 0.10) rather than carried as a literal, because the
        /// wording belongs to the module and this surface has four languages. A module that declares a key it
        /// has not translated falls back to the key itself, which is visibly wrong in the module's own
        /// release rather than silently English here.
        /// </remarks>
        public string Title { get; set; } = string.Empty;

        /// <summary>Which control to offer — the closed set of ABI 1.3, as its name.</summary>
        public string Input { get; set; } = nameof(ModuleAbi.PerUserEnrolmentInput.Certificate);

        /// <summary>Whether the tenant has the surface switched on.</summary>
        /// <remarks>
        /// <b>Present-and-false rather than absent</b>, which is the one place this resource deliberately
        /// departs from "a missing affordance means not available" (ADR 0543). The difference matters to the
        /// person reading it: absent means this installation does not do this at all, while false means an
        /// administrator could turn it on — and a client can say so instead of showing nothing and leaving
        /// somebody to wonder.
        /// </remarks>
        public bool Enabled { get; set; }

        /// <summary>The field the credential is sent as, inside the POSTed object.</summary>
        public string Field { get; set; } = string.Empty;

        /// <summary>The field a human-readable label is sent as, or empty when the endpoint takes none.</summary>
        public string LabelField { get; set; } = string.Empty;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var items = new List<SelfEnrolmentResource>();
        var culture = HttpContext.Features.Get<IRequestCultureFeature>()?.RequestCulture.UICulture
            ?? CultureInfo.CurrentUICulture;

        foreach (var loaded in modules)
        {
            if (loaded.Module.PerUserCertificateEnrolment is not { } declaration)
            {
                continue;
            }

            // An inactive module's behaviour is absent, not refused (ADR 0740) — so it is absent here too,
            // rather than listed as something that could be switched on.
            if (!await ModuleActivationCheck.IsActiveAsync(
                    dbContext, loaded.Module.ModuleId, DateTimeOffset.UtcNow, cancellationToken))
            {
                continue;
            }

            var enabled = await GateOpenAsync(loaded.Module.ModuleId, declaration.GateSetting, cancellationToken);

            var item = new SelfEnrolmentResource
            {
                ModuleId = loaded.Module.ModuleId,
                Title = Title(loaded, declaration.TitleKey, culture),
                Input = declaration.Input.ToString(),
                Enabled = enabled,
                Field = declaration.Field,
                LabelField = declaration.LabelField,
            };

            // THE ADDRESS IS ADVERTISED ONLY WHEN THE SURFACE IS OPEN. A rel that is present and refuses is
            // the lying affordance ADR 0543 exists to prevent, and "switched off" is exactly "not available
            // to you, here, now" — while `enabled` still says WHY nothing is offered, which an absent row
            // could not.
            item.Links = enabled
                ? [new Link("enrol", declaration.Route, "POST")]
                : [];

            items.Add(item);
        }

        return Ok(new SelfEnrolmentsResource
        {
            Items = [.. items.OrderBy(i => i.ModuleId, StringComparer.Ordinal)],
            Links = [new Link("self", "/api/me/self-enrolments", "GET")],
        });
    }

    // Standing convention: every GET action gets a companion HEAD action of its own.
    [HttpHead]
    public IActionResult Head() => NoContent();

    /// <summary>Whether the tenant has switched the declared gate setting on.</summary>
    /// <remarks>
    /// <b>Evaluated here rather than left to the client</b> (ADR 0864): the gate is a module SETTING, and
    /// reading a tenant's module settings needs tenant-administrator rights — which the person this surface
    /// serves does not have. A client that had to check for itself could not.
    /// <para>
    /// Unset is <b>off</b>. The first module's own default is off by owner decision (ADR 0813's reasoning:
    /// self-service registration is closed for exactly the tenants an envelope client serves), and a setting
    /// nobody has touched must not be read as consent.
    /// </para>
    /// </remarks>
    private async Task<bool> GateOpenAsync(string moduleId, string gateSetting, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(gateSetting))
        {
            // A declaration with no gate is open: the module is saying the surface has no switch, which is a
            // legitimate thing to declare and must not be read as "switched off".
            return true;
        }

        var value = await dbContext.ModuleSettingValues
            .Where(v => v.ModuleId == moduleId && v.Key == gateSetting)
            .Select(v => v.Value)
            .FirstOrDefaultAsync(cancellationToken);

        return bool.TryParse(value, out var on) && on;
    }

    /// <summary>The module's own wording for what is being enrolled, in the request's language.</summary>
    private string Title(ModuleLoader.LoadedModule loaded, string titleKey, CultureInfo culture)
    {
        if (string.IsNullOrWhiteSpace(titleKey))
        {
            return loaded.Module.DisplayName;
        }

        return ModuleTextResolver.Resolve([loaded], loaded.Module.ModuleId, titleKey, arg0: null, culture)
            is { } resolved
            ? resolved.Template
            : titleKey;
    }
}
