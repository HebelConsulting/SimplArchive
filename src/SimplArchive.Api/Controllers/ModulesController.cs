using System.Text;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Errors.Exceptions.Modules;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Modules;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// The tenant administrator's industry-module surface (ADRs 0740/0741/0743): what this host carries, what
/// this tenant has activated, and the activation act itself — PUT-ing a filed license document's id onto a
/// module's license. Tenant-admin only, like the sibling tenant-settings surface it is advertised from.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/modules")]
[Authorize]
public class ModulesController : ControllerBase
{
    // A license artefact is a few hundred bytes of JSON; anything past this is the wrong document (a
    // scan, a PDF) and is refused before being slurped into memory.
    private const int MaxLicenseBytes = 64 * 1024;

    private readonly SimplArchiveDbContext _dbContext;
    private readonly ICurrentTenantAccessor _currentTenantAccessor;
    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly IUserSystemRightsResolver _userSystemRights;
    private readonly IObjectStorageClient _objectStorage;
    private readonly IReadOnlyList<ModuleLoader.LoadedModule> _modules;
    private readonly ModuleActivationService _activation;
    private readonly IAuditRecorder _audit;
    private readonly ITransitEncryptor _transit;
    private readonly StateMachineCatalog _machines;

    public ModulesController(
        SimplArchiveDbContext dbContext,
        ICurrentTenantAccessor currentTenantAccessor,
        ICurrentUserAccessor currentUserAccessor,
        IUserSystemRightsResolver userSystemRights,
        IObjectStorageClient objectStorage,
        IReadOnlyList<ModuleLoader.LoadedModule> modules,
        ModuleActivationService activation,
        IAuditRecorder audit,
        ITransitEncryptor transit,
        StateMachineCatalog machines)
    {
        _dbContext = dbContext;
        _currentTenantAccessor = currentTenantAccessor;
        _currentUserAccessor = currentUserAccessor;
        _userSystemRights = userSystemRights;
        _objectStorage = objectStorage;
        _modules = modules;
        _activation = activation;
        _audit = audit;
        _transit = transit;
        _machines = machines;
    }

    public class ModuleResource : HypermediaResource
    {
        public string ModuleId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public int AbiMajorVersion { get; set; }

        /// <summary>
        /// WHICH BUILD of the module is loaded — the assembly's informational version, in practice
        /// <c>1.0.0+&lt;git sha&gt;</c>.
        /// </summary>
        /// <remarks>
        /// <b>Read the sha, not the number.</b> Every module builds as <c>1.0.0</c> (no module declares a
        /// <c>&lt;Version&gt;</c>, so that is the SDK default and it is the same for all of them); the
        /// <c>+sha</c> suffix is what identifies the build. When modules become versioned packages (#1246) the
        /// number starts carrying information too.
        ///
        /// <para>It is here because a stale module is otherwise INVISIBLE: it loads, seeds its masks, registers
        /// its controllers and serves requests, and only features added after the deployed build are missing —
        /// which reads as "never implemented" rather than "not deployed". The kiosk ran a build four releases
        /// old and the only way to establish that was comparing file mtimes and SHA-256 sums off the host
        /// (#1242). Null when the assembly carries no such attribute.</para>
        /// </remarks>
        public string? Build { get; set; }

        /// <summary>Whether this host carries the module's code. False on a row whose module was removed
        /// from disk — the activation (and the tenant's data) outlives the code, ADR 0740.</summary>
        public bool Installed { get; set; }

        /// <summary>Whether this tenant has ever filed a valid license (the activation row exists).</summary>
        public bool Activated { get; set; }

        /// <summary>Whether the behaviour is on right now — derived, never stored (ADR 0740).</summary>
        public bool Active { get; set; }

        /// <summary>Whether the contract has ended and only the grace period carries the behaviour.</summary>
        public bool InGrace { get; set; }

        public DateTimeOffset? SupportContractEndDate { get; set; }

        /// <summary>The instant the grace runs out and the module deactivates itself.</summary>
        public DateTimeOffset? DeactivatesAt { get; set; }

        public Guid? LicenseDocumentId { get; set; }

        public DateTimeOffset? ActivatedAt { get; set; }

        /// <summary>
        /// How many of this module's on-demand content sources are currently failing to refresh, and since
        /// when (ADR 0811). Zero means every source this module feeds is healthy.
        /// </summary>
        /// <remarks>
        /// Here for the reason <see cref="Build"/> is: without it the state is INVISIBLE. A populate hook
        /// degrades to "serve what is already filed" when its fetch fails — deliberately, so a failed
        /// enrichment cannot break somebody else's read — and the only record was a log line in the
        /// operator's collector, which a tenant administrator cannot see. A dead weather feed therefore
        /// looked exactly like a quiet one, indefinitely, inside an installation where every other check was
        /// green. This is the audit-webhook delivery-health line applied to module content.
        /// </remarks>
        public int FailingContentSources { get; set; }

        /// <summary>When the oldest current failure episode began — the fact an administrator acts on, where
        /// a count alone is not. Null when nothing is failing.</summary>
        public DateTimeOffset? ContentFailingSince { get; set; }

        /// <summary>The most recent failure's message, truncated. A message, never a payload (ADR 0626).</summary>
        public string? ContentLastError { get; set; }
    }

    public class ModuleListResource : HypermediaResource
    {
        public List<ModuleResource> Items { get; set; } = [];
    }

    public class LicenseDocumentResource : HypermediaResource
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }

        /// <summary>The stamped Module field — the VERIFIED claim's projection; empty until a license
        /// has been through a successful activation (the JSON inside stays the only truth).</summary>
        public string? Module { get; set; }

        /// <summary>The stamped "Valid until" field (yyyy-MM-dd), same projection.</summary>
        public string? ValidUntil { get; set; }
    }

    public class LicenseDocumentListResource : HypermediaResource
    {
        public List<LicenseDocumentResource> Items { get; set; } = [];
    }

    public class ActivateModuleRequest
    {
        /// <summary>The filed license document (ADR 0743: the artefact is an ordinary document, filed
        /// wherever the administrator chose; activation references it rather than inventing a location).</summary>
        public Guid LicenseDocumentId { get; set; }
    }

    // Unpaginated by design: the list enumerates CODE this host carries plus this tenant's few activation
    // rows — bounded like the tenant-settings groups, not like a document listing.
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        await IsTenantAdminAsync(cancellationToken)
            ? Ok(await BuildListAsync(cancellationToken))
            : Forbid();

    [HttpHead]
    public async Task<IActionResult> Head(CancellationToken cancellationToken) =>
        await IsTenantAdminAsync(cancellationToken) ? NoContent() : Forbid();

    /// <summary>The filed license artefacts: documents wearing the well-known Module-license mask, newest
    /// first — what the Activate/Renew dialog offers. Capped, not cursor-paginated: a tenant holds a few
    /// licenses, and the newest fifty is already forty-eight more than the realistic case.</summary>
    [HttpGet("license-documents")]
    public async Task<IActionResult> ListLicenseDocuments(CancellationToken cancellationToken) =>
        await IsTenantAdminAsync(cancellationToken)
            ? Ok(await BuildLicenseDocumentListAsync(cancellationToken))
            : Forbid();

    [HttpHead("license-documents")]
    public async Task<IActionResult> HeadLicenseDocuments(CancellationToken cancellationToken) =>
        await IsTenantAdminAsync(cancellationToken) ? NoContent() : Forbid();

    /// <summary>
    /// Rebuilds one of the module's projections from documents (ADR 0738) — the operator guarantee that a
    /// read model is never the only copy of anything, as a button-press: the support case's first answer.
    /// </summary>
    /// <remarks>Tenant-admin, and only where the module is ACTIVE (the same absence semantics as its
    /// routes); an unknown projection 404s — the rebuilder registry, not this controller, says what
    /// exists. The rebuilders come from the module's own DI registrations, scoped to this request's
    /// tenant like every other module service.</remarks>
    [HttpPost("{moduleId}/rebuild/{projectionName}")]
    public async Task<IActionResult> RebuildProjection(
        string moduleId, string projectionName,
        [FromServices] IEnumerable<SimplArchive.ModuleAbi.IModuleProjectionRebuilder> rebuilders,
        CancellationToken cancellationToken)
    {
        if (!await IsTenantAdminAsync(cancellationToken))
        {
            return Forbid();
        }

        if (!await ModuleActivationCheck.IsActiveAsync(_dbContext, moduleId, DateTimeOffset.UtcNow, cancellationToken))
        {
            throw new ModuleNotActiveException(moduleId);
        }

        var rebuilder = rebuilders.FirstOrDefault(r => r.ProjectionNames.Contains(projectionName, StringComparer.Ordinal));
        if (rebuilder is null)
        {
            return NotFound();
        }

        // The rebuild derives from documents through the facade — module code, module eyes (ADR 0736).
        HttpContext.RequestServices.GetRequiredService<ModuleIdentityAccessor>().ModuleId = moduleId;
        await rebuilder.RebuildAsync(projectionName, cancellationToken);
        return NoContent();
    }

    /// <summary>The activation act (ADRs 0740/0743): verify the filed license, seed the module's masks,
    /// upsert the activation row. Renewal is the same PUT with the newly filed license's id.</summary>
    [HttpPut("{moduleId}/license")]
    public async Task<IActionResult> PutLicense(string moduleId, [FromBody] ActivateModuleRequest request, CancellationToken cancellationToken)
    {
        if (!await IsTenantAdminAsync(cancellationToken))
        {
            return Forbid();
        }

        // The LOADED module, not just its contract: the activation response reports the build too, and a field
        // that is populated on one surface and null on another is one nobody trusts on either.
        var loaded = _modules.FirstOrDefault(m => string.Equals(m.Module.ModuleId, moduleId, StringComparison.Ordinal));
        var module = loaded?.Module
            ?? throw new ModuleNotInstalledException(moduleId);

        var document = await _dbContext.Documents
            .SingleOrDefaultAsync(d => d.Id == request.LicenseDocumentId, cancellationToken);
        if (document is null)
        {
            return NotFound();
        }

        var version = await CurrentVersion.ResolveAsync(
            _dbContext.DocumentVersions, document.Id, document.CurrentVersionId, cancellationToken);
        if (version is null)
        {
            throw new ModuleLicenseRejectedException("The license document has no confirmed content version.");
        }

        var licenseJson = await ReadLicenseAsync(version.ObjectKey, cancellationToken);
        ModuleActivation activation;
        try
        {
            activation = await _activation.ActivateAsync(
                module, licenseJson, document.Id, _currentTenantAccessor.TenantId!.Value,
                _currentUserAccessor.UserId, cancellationToken);
        }
        catch (ModuleLicenseException exception)
        {
            // One exception type, carried verbatim — the domain factories already name the precise refusal.
            throw new ModuleLicenseRejectedException(exception.Message);
        }

        await _audit.RecordAsync(AuditActions.ModuleActivated, "Module", activation.Id, module.ModuleId,
            $"Support contract through {activation.SupportContractEndDate:yyyy-MM-dd}; license document {document.Id}",
            cancellationToken: cancellationToken);

        return Ok(ToResource(module.ModuleId, module.DisplayName, module.AbiMajorVersion, installed: true, activation,
            hasSettings: DeclaredSettings(module.ModuleId).Count > 0, build: loaded?.Build,
            health: (await ContentHealthAsync(cancellationToken)).GetValueOrDefault(module.ModuleId)));
    }

    /// <summary>
    /// What this module declared it needs configuring, plus what is configured (ADR 0772). A secret's VALUE
    /// is never here — only whether one is set, which is the audit-webhook secret's precedent.
    /// </summary>
    [HttpGet("{moduleId}/settings")]
    public async Task<IActionResult> GetSettings(string moduleId, CancellationToken cancellationToken)
    {
        if (!await IsTenantAdminAsync(cancellationToken))
        {
            return Forbid();
        }

        return Ok(await BuildSettingsAsync(moduleId, cancellationToken));
    }

    [HttpHead("{moduleId}/settings")]
    public async Task<IActionResult> HeadSettings(string moduleId, CancellationToken cancellationToken)
    {
        if (!await IsTenantAdminAsync(cancellationToken))
        {
            return Forbid();
        }

        await BuildSettingsAsync(moduleId, cancellationToken);   // 404s for an uninstalled module, as GET does
        return NoContent();
    }

    /// <summary>
    /// Writes the values present in the body — a MERGE, deliberately unlike the tenant-settings PUT.
    /// </summary>
    /// <remarks>
    /// A full replacement is unusable here: a client cannot read a secret back, so it could not resend one,
    /// and every unsent secret would be blanked by a form that only meant to change the endpoint beside it.
    /// So an absent key is "leave it alone" and an explicit null is "clear it" — the two intentions a client
    /// actually has.
    /// </remarks>
    [HttpPut("{moduleId}/settings")]
    public async Task<IActionResult> PutSettings(
        string moduleId, [FromBody] PutModuleSettingsRequest request, CancellationToken cancellationToken)
    {
        if (!await IsTenantAdminAsync(cancellationToken))
        {
            return Forbid();
        }

        var declared = DeclaredSettings(moduleId);
        var tenantId = _currentTenantAccessor.TenantId!.Value;
        var existing = await _dbContext.ModuleSettingValues
            .Where(v => v.ModuleId == moduleId)
            .ToListAsync(cancellationToken);

        var changed = new List<string>();
        foreach (var (key, value) in request.Values ?? new Dictionary<string, string?>())
        {
            // An undeclared key is refused rather than stored: a store that accepts anything becomes the
            // free-form bag this was designed not to be, and a typo would silently never be read back.
            var setting = declared.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.Ordinal))
                ?? throw new ModuleSettingNotDeclaredException(moduleId, key);

            // A Boolean means one of two things or it means nothing. Refusing anything else here is what keeps
            // the read side a straight equality test: a stored "yes" or "1" would read as FALSE at the moment
            // it matters, and silently — the tenant would see the toggle on and the behaviour off.
            if (setting.Kind == ModuleAbi.ModuleSettingKind.Boolean && !string.IsNullOrEmpty(value)
                && !string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
            {
                throw new ModuleSettingValueInvalidException(moduleId, key);
            }

            var row = existing.FirstOrDefault(v => string.Equals(v.Key, key, StringComparison.Ordinal));

            if (string.IsNullOrEmpty(value))
            {
                if (row is not null)
                {
                    _dbContext.ModuleSettingValues.Remove(row);   // an explicit null clears
                    changed.Add(key);
                }

                continue;
            }

            // Encrypt on the way in, and record on the ROW that it is encrypted (never re-derive that from
            // the live declaration — see ModuleSettingValue's remarks).
            var stored = setting.IsSecret ? await _transit.EncryptAsync(value, cancellationToken) : value;

            if (row is null)
            {
                _dbContext.ModuleSettingValues.Add(new ModuleSettingValue
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    ModuleId = moduleId,
                    Key = key,
                    Value = stored,
                    IsSecret = setting.IsSecret,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    UpdatedByUserId = _currentUserAccessor.UserId,
                });
            }
            else
            {
                row.Value = stored;
                row.IsSecret = setting.IsSecret;
                row.UpdatedAt = DateTimeOffset.UtcNow;
                row.UpdatedByUserId = _currentUserAccessor.UserId;
            }

            changed.Add(key);
        }

        if (changed.Count > 0)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);

            // The KEYS that changed, never their values — an audit trail that leaked a credential would be
            // worse than none, and it is the durable, exportable, webhook-streamed one.
            await _audit.RecordAsync(AuditActions.ModuleSettingsUpdated, "Module", Guid.Empty, moduleId,
                $"Changed: {string.Join(", ", changed.OrderBy(k => k, StringComparer.Ordinal))}",
                cancellationToken: cancellationToken);
        }

        return Ok(await BuildSettingsAsync(moduleId, cancellationToken));
    }

    /// <summary>The module's declarations, or a 404 when nothing installed here answers to that id.</summary>
    /// <remarks>
    /// <para>
    /// The ONE place the declared set is composed, which is what lets the host add to it: both the form and
    /// the PUT's undeclared-key refusal read this, so a setting appended here is rendered, validated and
    /// storable without a second edit. Two copies of the composition is how a key becomes renderable and
    /// unsaveable at the same time.
    /// </para>
    /// <para>
    /// The appended one is the protocol-read toggle (ABI 0.27, ADR 0810): the host renders it exactly when
    /// this module declares a populate hook eligible for a WebDAV/IMAP read, because the host is what reads
    /// the answer back on a PROPFIND. A module declaring its own key would leave the host guessing the name.
    /// </para>
    /// </remarks>
    private IReadOnlyList<ModuleAbi.ModuleSetting> DeclaredSettings(string moduleId)
    {
        var module = _modules.FirstOrDefault(m => string.Equals(m.Module.ModuleId, moduleId, StringComparison.Ordinal))?.Module
            ?? throw new ModuleNotInstalledException(moduleId);

        var declaresProtocolRead = _machines.Machines.Values.Any(machine =>
            string.Equals(machine.ModuleId, moduleId, StringComparison.Ordinal)
            && machine.Transitions.Values.Any(t => t is { AutoRefreshOnOpen: true, ProtocolRead: ModuleAbi.ProtocolReadRefresh.WhenTenantEnables }));

        return declaresProtocolRead
            ? [.. module.Settings, new ModuleAbi.ModuleSetting(
                ModuleAbi.ProtocolReadRefreshSetting.Key,
                "Refresh content when a drive or mail client opens the folder",
                Description: "This module fetches its content on demand when the folder is opened. The two SimplArchive "
                    + "clients always do so; enable this to let a mounted drive or a mail client do it too, otherwise "
                    + "those surfaces show only what is already filed.")
                { Kind = ModuleAbi.ModuleSettingKind.Boolean }]
            : module.Settings;
    }

    private async Task<ModuleSettingsResource> BuildSettingsAsync(string moduleId, CancellationToken cancellationToken)
    {
        var declared = DeclaredSettings(moduleId);
        var configured = await _dbContext.ModuleSettingValues
            .Where(v => v.ModuleId == moduleId)
            .Select(v => new { v.Key, v.Value, v.IsSecret })
            .ToListAsync(cancellationToken);

        return new ModuleSettingsResource
        {
            ModuleId = moduleId,
            Items = declared.Select(setting =>
            {
                var stored = configured.FirstOrDefault(v => string.Equals(v.Key, setting.Key, StringComparison.Ordinal));
                return new ModuleSettingResource
                {
                    Key = setting.Key,
                    Label = setting.Label,
                    Description = setting.Description,
                    IsSecret = setting.IsSecret,
                    Kind = setting.Kind.ToString(),
                    HasValue = stored is not null,
                    // A secret's value never crosses the wire; a plain setting's does, or the form could not
                    // show what it is about to change.
                    Value = setting.IsSecret ? null : stored?.Value,
                };
            }).ToList(),
            Links = [new Link("self", $"/api/modules/{moduleId}/settings", "GET")],
        };
    }

    public class ModuleSettingsResource : HypermediaResource
    {
        public string ModuleId { get; set; } = string.Empty;

        public List<ModuleSettingResource> Items { get; set; } = [];
    }

    public class ModuleSettingResource
    {
        public string Key { get; set; } = string.Empty;

        public string Label { get; set; } = string.Empty;

        public string? Description { get; set; }

        public bool IsSecret { get; set; }

        /// <summary>What the value holds, so the form draws a checkbox rather than a text box (ABI 0.27).
        /// Serialized as the enum's NAME, which is what a client matches on.</summary>
        public string Kind { get; set; } = nameof(ModuleAbi.ModuleSettingKind.Text);

        /// <summary>Whether a value is configured — the only thing reported for a secret.</summary>
        public bool HasValue { get; set; }

        /// <summary>The configured value, for a non-secret setting only.</summary>
        public string? Value { get; set; }
    }

    public class PutModuleSettingsRequest
    {
        /// <summary>Key → value. An absent key is left alone; an explicit null or empty clears one.</summary>
        public Dictionary<string, string?>? Values { get; set; }
    }

    private async Task<LicenseDocumentListResource> BuildLicenseDocumentListAsync(CancellationToken cancellationToken)
    {
        // Filter and order on the ENTITY before projecting (the EF-translation gotcha): documents whose
        // worn mask version belongs to the well-known Module-license mask.
        var documents = await _dbContext.Documents
            .Where(d => _dbContext.MaskVersions
                .Any(v => v.Id == d.MaskVersionId && v.MaskId == WellKnownMaskIds.License))
            .OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Id)
            .Take(50)
            .Select(d => new { d.Id, d.Name, d.CreatedAt })
            .ToListAsync(cancellationToken);

        var ids = documents.Select(d => d.Id).ToList();
        var fields = await _dbContext.FieldValues
            .Where(v => ids.Contains(v.DocumentId))
            .Join(_dbContext.FieldDefinitions, v => v.FieldDefinitionId, f => f.Id,
                (v, f) => new { v.DocumentId, f.Name, v.Value })
            .Where(x => x.Name == "Module" || x.Name == "Valid until")
            .ToListAsync(cancellationToken);
        var byDocument = fields.ToLookup(f => f.DocumentId);

        return new LicenseDocumentListResource
        {
            Items = documents.Select(d => new LicenseDocumentResource
            {
                Id = d.Id,
                Name = d.Name,
                CreatedAt = d.CreatedAt,
                Module = byDocument[d.Id].FirstOrDefault(f => f.Name == "Module")?.Value,
                ValidUntil = byDocument[d.Id].FirstOrDefault(f => f.Name == "Valid until")?.Value,
            }).ToList(),
            Links = [new Link("self", "/api/modules/license-documents", "GET")],
        };
    }

    private async Task<ModuleListResource> BuildListAsync(CancellationToken cancellationToken)
    {
        var activations = await _dbContext.ModuleActivations
            .OrderBy(a => a.ModuleId)
            .ToListAsync(cancellationToken);
        var byModuleId = activations.ToDictionary(a => a.ModuleId, StringComparer.Ordinal);
        var health = await ContentHealthAsync(cancellationToken);

        var items = _modules
            .Select(m => ToResource(
                m.Module.ModuleId, m.Module.DisplayName, m.Module.AbiMajorVersion, installed: true,
                byModuleId.GetValueOrDefault(m.Module.ModuleId), hasSettings: DeclaredSettings(m.Module.ModuleId).Count > 0,
                build: m.Build, health: health.GetValueOrDefault(m.Module.ModuleId)))
            .ToList();

        // Activation rows whose module is no longer on disk: the data outlives the code (ADR 0740), and an
        // administrator wondering where the behaviour went deserves to see the row rather than nothing.
        var loadedIds = _modules.Select(m => m.Module.ModuleId).ToHashSet(StringComparer.Ordinal);
        items.AddRange(activations
            .Where(a => !loadedIds.Contains(a.ModuleId))
            // Not installed: no code here to declare settings, so no form to offer.
            .Select(a => ToResource(a.ModuleId, a.ModuleId, abiMajorVersion: 0, installed: false, a, hasSettings: false,
                health: health.GetValueOrDefault(a.ModuleId))));

        return new ModuleListResource
        {
            Items = items.OrderBy(i => i.ModuleId, StringComparer.Ordinal).ToList(),
            Links =
            [
                new Link("self", "/api/modules", "GET"),
                // What the Activate/Renew dialog lists (ADR 0557: the collection's own affordances are
                // captured where the collection is read).
                new Link("license-documents", "/api/modules/license-documents", "GET"),
            ],
        };
    }

    /// <summary>One module's content-health answer: how many sources are failing, since when, and the last
    /// message. Absence of a row means healthy, so a module with nothing failing simply has no entry.</summary>
    private sealed record ContentHealthSummary(int Failing, DateTimeOffset Since, string LastError);

    /// <summary>
    /// The health of every module's content sources, in ONE query rather than one per module.
    /// </summary>
    /// <remarks>
    /// Per module, aggregated from the per-SOURCE rows. The counting happens per source on purpose (ADR
    /// 0811) — counting consecutive failures per module would let one permanently-broken source hide behind
    /// its healthy siblings — but what an administrator acts on is the module, so the rows are folded here.
    /// </remarks>
    private async Task<Dictionary<string, ContentHealthSummary>> ContentHealthAsync(CancellationToken cancellationToken)
    {
        var rows = await _dbContext.ModuleContentHealth
            .Select(h => new { h.ModuleId, h.FirstFailureAt, h.LastFailureAt, h.LastError })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(h => h.ModuleId, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => new ContentHealthSummary(
                    g.Count(),
                    g.Min(h => h.FirstFailureAt),
                    // The most RECENT message, not the oldest: an administrator arriving at this line wants
                    // what is wrong now, and an episode's first error is often the least informative one.
                    g.OrderByDescending(h => h.LastFailureAt).First().LastError),
                StringComparer.Ordinal);
    }

    private static ModuleResource ToResource(
        string moduleId, string displayName, int abiMajorVersion, bool installed, ModuleActivation? activation,
        bool hasSettings, string? build = null, ContentHealthSummary? health = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new ModuleResource
        {
            FailingContentSources = health?.Failing ?? 0,
            ContentFailingSince = health?.Since,
            ContentLastError = health?.LastError,
            ModuleId = moduleId,
            DisplayName = displayName,
            AbiMajorVersion = abiMajorVersion,
            Build = build,
            Installed = installed,
            Activated = activation is not null,
            Active = installed && activation is not null && ModuleActivationPolicy.IsActive(activation, now),
            InGrace = activation is not null && ModuleActivationPolicy.IsInGrace(activation, now),
            SupportContractEndDate = activation?.SupportContractEndDate,
            DeactivatesAt = activation is null ? null : ModuleActivationPolicy.DeactivatesAt(activation),
            LicenseDocumentId = activation?.LicenseDocumentId,
            ActivatedAt = activation?.ActivatedAt,
            // The activation act is only reachable where the code to activate exists; a not-installed row
            // has nothing to license (ADR 0543: a missing rel means "not available to you, here, now").
            // Both actions are only reachable where the code exists; a not-installed row has nothing to
            // license and declares no settings (ADR 0543: a missing rel means "not available to you, here,
            // now"). `settings` is ONE rel for GET and PUT on the same address (ADR 0719) — and it is
            // withheld from a module that declares none, so no empty form is ever offered.
            Links = installed
                ?
                [
                    new Link("license", $"/api/modules/{moduleId}/license", "PUT"),
                    .. hasSettings
                        ? new[] { new Link("settings", $"/api/modules/{moduleId}/settings", "GET") }
                        : [],
                ]
                : [],
        };
    }

    private async Task<string> ReadLicenseAsync(string objectKey, CancellationToken cancellationToken)
    {
        await using var stream = await _objectStorage.GetObjectAsync(objectKey, cancellationToken);
        // Bounded read: stop at the cap + 1 rather than slurping whatever the document turns out to be.
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxLicenseBytes)
            {
                throw new ModuleLicenseRejectedException("The document is too large to be a license artefact.");
            }
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private async Task<bool> IsTenantAdminAsync(CancellationToken cancellationToken) =>
        _currentUserAccessor.UserId is Guid userId
        && (await _userSystemRights.GetEffectiveSystemRightsAsync(userId, cancellationToken)).IsTenantAdmin;
}
