using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// Implements ADR "Metadata / index-field model"'s "admins define document types (masks)" behavior — see
/// ADR "Mask creation endpoint". POST creates a Mask plus its first MaskVersion and FieldDefinitions in
/// one request; GET/HEAD read back the current version. No "list all masks" endpoint yet — deferred to
/// the mask-assignment follow-up, which is what actually needs to enumerate available masks. Gated on the
/// dedicated CanManageMasks right — either a ServiceAccount or a logged-in User (see ADR "User support for
/// ServiceAccount/User/Group/Mask management endpoints").
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/masks")]
[Authorize]
public class MasksController : ControllerBase
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly ICurrentServiceAccountAccessor _currentServiceAccountAccessor;
    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly ICurrentTenantAccessor _currentTenantAccessor;
    private readonly IUserSystemRightsResolver _userSystemRights;

    public MasksController(
        SimplArchiveDbContext dbContext,
        ICurrentServiceAccountAccessor currentServiceAccountAccessor,
        ICurrentUserAccessor currentUserAccessor,
        ICurrentTenantAccessor currentTenantAccessor,
        IUserSystemRightsResolver userSystemRights)
    {
        _dbContext = dbContext;
        _currentServiceAccountAccessor = currentServiceAccountAccessor;
        _currentUserAccessor = currentUserAccessor;
        _currentTenantAccessor = currentTenantAccessor;
        _userSystemRights = userSystemRights;
    }

    // Plain mutable classes, not records — System.Xml.Serialization.XmlSerializer (ADR "JSON/XML content
    // negotiation") needs a parameterless constructor and settable properties.
    public class FieldDefinitionRequest
    {
        public string Name { get; set; } = "";

        public FieldDataType DataType { get; set; }

        public bool IsRequired { get; set; }

        /// <summary>Whether the field holds many values of its type rather than one (#703).</summary>
        public bool IsList { get; set; }

        public string? FormatPattern { get; set; }

        public int? MaxTextLength { get; set; }

        public string? MinValue { get; set; }

        public string? MaxValue { get; set; }
    }

    public class CreateMaskRequest
    {
        public string Name { get; set; } = "";

        // The approval-review SLA (days) for documents of this mask type (ADR "Workflow escalation / SLA
        // reminders"). Null = no SLA / no deadline tracking.
        public int? ReviewSlaDays { get; set; }

        // The records-retention period (years) for documents of this mask type (ADR "Retention policies
        // (auto-disposition)"). Null = no retention → never auto-disposed.
        public int? RetentionYears { get; set; }

        // The upload-time default sensitivity label (ADR "Configurable sensitivity labels + upload defaults") —
        // applied to a document auto-classified as this type if it has no label yet. Null = no default.
        public Guid? DefaultSensitivityLabelId { get; set; }

        public List<FieldDefinitionRequest> Fields { get; set; } = [];
    }

    public class FieldDefinitionResource
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = "";

        public string DataType { get; set; } = "";

        public bool IsRequired { get; set; }

        /// <summary>Whether the field holds many values of its type rather than one (#703) — what tells a
        /// client to draw a list editor instead of a single-value one.</summary>
        public bool IsList { get; set; }

        public string? FormatPattern { get; set; }

        public int? MaxTextLength { get; set; }

        public string? MinValue { get; set; }

        public string? MaxValue { get; set; }
    }

    public class MaskResource : HypermediaResource
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = "";

        public int VersionNumber { get; set; }

        public int? ReviewSlaDays { get; set; }

        public int? RetentionYears { get; set; }

        public Guid? DefaultSensitivityLabelId { get; set; }

        public List<FieldDefinitionResource> Fields { get; set; } = [];
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateMaskRequest request, CancellationToken cancellationToken)
    {
        if (!await CanManageMasksAsync(cancellationToken))
        {
            return Forbid();
        }

        var tenantId = _currentTenantAccessor.TenantId!.Value;

        var mask = new Mask { Id = Guid.NewGuid(), TenantId = tenantId, CreatedAt = DateTimeOffset.UtcNow };
        _dbContext.Masks.Add(mask);

        // VersionNumber/IsCurrent are left unset — SimplArchiveDbContext.SaveChanges assigns them
        // automatically (ADR "Mask name uniqueness across versions"). Never set manually.
        var maskVersion = new MaskVersion { Id = Guid.NewGuid(), TenantId = tenantId, MaskId = mask.Id, Name = request.Name, ReviewSlaDays = request.ReviewSlaDays, RetentionYears = request.RetentionYears, DefaultSensitivityLabelId = request.DefaultSensitivityLabelId, CreatedAt = DateTimeOffset.UtcNow };
        _dbContext.MaskVersions.Add(maskVersion);

        var fieldDefinitions = request.Fields.Select(f => new FieldDefinition
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            MaskVersionId = maskVersion.Id,
            Name = f.Name,
            DataType = f.DataType,
            IsRequired = f.IsRequired,
            IsList = f.IsList,
            FormatPattern = f.FormatPattern,
            MaxTextLength = f.MaxTextLength,
            MinValue = f.MinValue,
            MaxValue = f.MaxValue,
            CreatedAt = DateTimeOffset.UtcNow,
        }).ToList();

        _dbContext.FieldDefinitions.AddRange(fieldDefinitions);

        await _dbContext.SaveChangesAsync(cancellationToken);

        var resource = BuildResource(mask.Id, maskVersion.Name, maskVersion.VersionNumber, maskVersion.ReviewSlaDays, maskVersion.RetentionYears, maskVersion.DefaultSensitivityLabelId, fieldDefinitions);

        return CreatedAtAction(nameof(Get), new { maskId = mask.Id }, resource);
    }

    public class MaskSummaryResource : HypermediaResource
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = "";

        public int VersionNumber { get; set; }

        /// <summary>
        /// True when this mask types a FOLDER — so it must never be offered for a filed document.
        /// </summary>
        /// <remarks>
        /// Named for the MASK, not the document: <c>IsFolder</c> already means "a document with no versions"
        /// throughout the clients, and conflating the two is how a picker ends up asking the wrong question.
        /// </remarks>
        public bool IsFolderMask { get; set; }

        /// <summary>
        /// The extensions that make this mask automatic. Non-empty means the user gets NO choice: the
        /// classifier assigns it on upload and containment then requires the matching collection.
        /// </summary>
        public List<string> FileExtensions { get; set; } = [];

        /// <summary>
        /// Whether a user may choose this mask for an ordinary filed document.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Decided by the SERVER and sent, not derived by each client — which is the whole point of #671. Both
        /// clients were inferring it, differently, and offering masks the containment invariant then refused,
        /// so the user learned about it from a failed save (#580).
        /// </para>
        /// <para>
        /// <b>Three reasons a mask is not choosable</b>, and it took a test to find the third. A folder mask
        /// types a folder. An extension-claimed mask is assigned by the classifier on upload. And a mask whose
        /// primary location is CONSTRAINED — <c>Note</c> lives only in a Notebook or a Section — is not
        /// choosable either, though it is neither of the first two. Deriving this from the two projected fields
        /// would have quietly offered Note, which is exactly the bug being fixed.
        /// </para>
        /// <para>
        /// The containment part still comes from static app knowledge (<c>WellKnownMaskIds.AdmittingFolders</c>),
        /// so it cannot yet describe a tenant-authored mask. #673 moves that into the model; when it lands, this
        /// property's third input changes source and nothing else here does.
        /// </para>
        /// </remarks>
        public bool IsFreelyAssignable { get; set; }
    }

    public class MaskListResource : HypermediaResource
    {
        public List<MaskSummaryResource> Masks { get; set; } = [];
    }

    // Lists the tenant's masks (current version of each) — for a picker to assign/change a document's mask.
    // Read-only, so any authenticated caller in the tenant (assigning a mask needs CanEditIndexData on the
    // document, not CanManageMasks). A small bounded catalog, so not paginated.
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var masks = await _dbContext.MaskVersions
            .Where(v => v.IsCurrent)
            .OrderBy(v => v.Name)
            .Select(v => new MaskSummaryResource { Id = v.MaskId, Name = v.Name, VersionNumber = v.VersionNumber })
            .ToListAsync(cancellationToken);

        // Assignability comes from the MASK, not the version: whether it types a folder and which extensions
        // claim it are identity-level facts (#671). Read for the whole page in two queries rather than per row.
        var ids = masks.Select(m => m.Id).ToList();
        var folderMasks = await _dbContext.Masks
            .Where(m => ids.Contains(m.Id) && m.IsFolderMask)
            .Select(m => m.Id)
            .ToListAsync(cancellationToken);
        var extensions = await _dbContext.MaskFileExtensions
            .Where(e => ids.Contains(e.MaskId))
            .Select(e => new { e.MaskId, e.Extension })
            .ToListAsync(cancellationToken);

        // Each summary addresses the full mask — its field definitions — so a client holding a row follows the
        // rel instead of rebuilding /masks/{id} (issue #416).
        foreach (var mask in masks)
        {
            mask.IsFolderMask = folderMasks.Contains(mask.Id);
            mask.FileExtensions = [.. extensions.Where(e => e.MaskId == mask.Id).Select(e => e.Extension).Order()];
            mask.IsFreelyAssignable = !mask.IsFolderMask
                && mask.FileExtensions.Count == 0
                && !WellKnownMaskIds.AdmittingFolders.ContainsKey(mask.Id);
            mask.Links = [new Link("self", $"/api/masks/{mask.Id}", "GET")];

            // NO create rel here, deliberately. Creating a typed folder needs a PARENT, which this resource does
            // not know — an href carrying a {parentId} placeholder would be a template the client substitutes
            // into, which is composing a URL wearing a rel's clothes (ADR 0543). The affordance belongs on the
            // document that would hold the new folder, beside the `create-child` rel that already gates
            // "New subfolder", because what may be created somewhere is a fact about that somewhere.
        }

        return Ok(new MaskListResource { Masks = masks, Links = [new Link("self", "/api/masks", "GET")] });
    }

    [HttpHead]
    public IActionResult HeadList() => NoContent();

    [HttpGet("{maskId:guid}")]
    public async Task<IActionResult> Get(Guid maskId, CancellationToken cancellationToken)
    {
        var version = await _dbContext.MaskVersions
            .Where(v => v.MaskId == maskId && v.IsCurrent)
            .SingleOrDefaultAsync(cancellationToken);

        if (version is null)
        {
            return NotFound();
        }

        var fields = await _dbContext.FieldDefinitions
            .Where(f => f.MaskVersionId == version.Id)
            .ToListAsync(cancellationToken);

        return Ok(BuildResource(maskId, version.Name, version.VersionNumber, version.ReviewSlaDays, version.RetentionYears, version.DefaultSensitivityLabelId, fields));
    }

    // Standing convention: every GET action gets a companion HEAD action — a separate action, not
    // relying on ASP.NET Core to strip GET's body automatically.
    [HttpHead("{maskId:guid}")]
    public async Task<IActionResult> Head(Guid maskId, CancellationToken cancellationToken)
    {
        var exists = await _dbContext.MaskVersions.AnyAsync(v => v.MaskId == maskId && v.IsCurrent, cancellationToken);

        return exists ? NoContent() : NotFound();
    }

    private static MaskResource BuildResource(Guid maskId, string name, int versionNumber, int? reviewSlaDays, int? retentionYears, Guid? defaultSensitivityLabelId, List<FieldDefinition> fields)
    {
        return new MaskResource
        {
            Id = maskId,
            Name = name,
            VersionNumber = versionNumber,
            ReviewSlaDays = reviewSlaDays,
            RetentionYears = retentionYears,
            DefaultSensitivityLabelId = defaultSensitivityLabelId,
            Fields = fields.Select(f => new FieldDefinitionResource
            {
                Id = f.Id,
                Name = f.Name,
                DataType = f.DataType.ToString(),
                IsRequired = f.IsRequired,
                IsList = f.IsList,
                FormatPattern = f.FormatPattern,
                MaxTextLength = f.MaxTextLength,
                MinValue = f.MinValue,
                MaxValue = f.MaxValue,
            }).ToList(),
            Links = [new Link("self", $"/api/masks/{maskId}", "GET")],
        };
    }

    // Checks ServiceAccount.CanManageMasks first, then User.CanManageMasks — see ADR "User support for
    // ServiceAccount/User/Group/Mask management endpoints".
    private async Task<bool> CanManageMasksAsync(CancellationToken cancellationToken)
    {
        if (_currentServiceAccountAccessor.ServiceAccountId is { } serviceAccountId)
        {
            return await _dbContext.ServiceAccounts
                .Where(s => s.Id == serviceAccountId)
                .Select(s => s.CanManageMasks)
                .SingleAsync(cancellationToken);
        }

        if (_currentUserAccessor.UserId is { } userId)
        {
            // Effective rights (own ∪ groups) — ADR "Enforce group system rights for members".
            return (await _userSystemRights.GetEffectiveSystemRightsAsync(userId, cancellationToken)).CanManageMasks;
        }

        return false;
    }
}
