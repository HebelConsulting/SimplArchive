using System.Text.RegularExpressions;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Errors;
using SimplArchive.Api.Errors.Exceptions.Annotations;
using SimplArchive.Api.Errors.Exceptions.Concurrency;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// Sticky notes / positional annotations pinned to a page of a document version — see
/// ADR "Document annotations (sticky notes)". Distinct from the per-document comment thread
/// (DocumentChatController): a note is pinned to a (page, x, y) spot on the rendered page and is editable.
///
/// Reading requires CanReadContent; creating (POST) + editing (PUT) require CanAnnotate (ADR "CanAnnotate
/// right") — edit is additionally author-only; deleting (DELETE) is the author OR a CanEditContent holder /
/// tenant admin. The list resource carries a CanCreate hint (= CanAnnotate) so a client can hide "Add note".
/// PUT/DELETE carry If-Match (the note is IConcurrencyTracked). Authorization accepts a ServiceAccount or a User.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/documents/{documentId:guid}/versions/{versionId:guid}/annotations")]
[Authorize]
public partial class DocumentAnnotationsController : ControllerBase
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly ICurrentServiceAccountAccessor _currentServiceAccountAccessor;
    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly IDocumentIndexQueue _indexQueue;

    public DocumentAnnotationsController(
        SimplArchiveDbContext dbContext,
        ICurrentServiceAccountAccessor currentServiceAccountAccessor,
        ICurrentUserAccessor currentUserAccessor,
        IDocumentIndexQueue indexQueue,
        IAuditRecorder audit,
        Documents.DocumentAccessService access)
    {
        _dbContext = dbContext;
        _currentServiceAccountAccessor = currentServiceAccountAccessor;
        _currentUserAccessor = currentUserAccessor;
        _indexQueue = indexQueue;
        _audit = audit;
        _access = access;
    }

    private readonly Documents.DocumentAccessService _access;
    private readonly IAuditRecorder _audit;

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColorRegex();

    // Plain mutable classes, not records — XmlSerializer (ADR "JSON/XML content negotiation").
    public class AnnotationResource : HypermediaResource
    {
        public Guid Id { get; set; }
        public int PageIndex { get; set; }
        // The markup kind as an int (Note=0/Highlight=1/Rectangle=2/Arrow=3), consistent with the Api's
        // enum-as-int convention (ADR "Annotation markup: highlight + shapes").
        public int Kind { get; set; }
        public double PositionX { get; set; }
        public double PositionY { get; set; }
        // The shape extent (null for a Note); box size for Highlight/Rectangle, signed end-offset for an Arrow.
        public double? Width { get; set; }
        public double? Height { get; set; }
        // A Freehand stroke's normalized path ("x,y x,y …"); null for every other kind (ADR 0525).
        public string? Points { get; set; }
        public string Text { get; set; } = "";
        public string Color { get; set; } = "";
        // How the text is rendered (ADR 0542); null when the annotation is unstyled or draws no text.
        public AnnotationTextStyleResource? TextStyle { get; set; }
        public string AuthorName { get; set; } = "";
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        // The optimistic-concurrency token to send back as If-Match on PUT/DELETE — embedded so the client
        // (which drag-moves notes) doesn't need a HEAD round-trip before each edit.
        public string Etag { get; set; } = "";
        // Client hints: whether this caller may edit (author) / delete (author or a CanEditContent holder).
        public bool CanEdit { get; set; }
        public bool CanDelete { get; set; }
    }

    // The text styling of a text-bearing annotation (ADR 0542). Used in BOTH directions — it is a value object
    // with the same shape going in and coming out, so a separate request twin would only duplicate it.
    public class AnnotationTextStyleResource
    {
        public string? FontFamily { get; set; }
        // Always positive; what it measures is SizeBasis's job, not a sign convention.
        public int? FontSizePx { get; set; }
        // 0 = CellHeight (includes internal leading), 1 = CharacterHeight — the Api's enum-as-int convention.
        public int? SizeBasis { get; set; }
        public bool Bold { get; set; }
        public bool Italic { get; set; }
        public bool Underline { get; set; }
        public bool Strikethrough { get; set; }
    }

    public class AnnotationListResource : HypermediaResource
    {
        public List<AnnotationResource> Annotations { get; set; } = [];

        // Whether this caller may create a note here (CanAnnotate) — lets the clients hide/disable "Add note".
        public bool CanCreate { get; set; }
    }

    public class CreateAnnotationRequest
    {
        public int PageIndex { get; set; }
        public int Kind { get; set; } // 0=Note; 1/2/3=Highlight/Rectangle/Arrow; 4/5/6/7=Stamp/Strikethrough/TextBox/Freehand
        public double PositionX { get; set; }
        public double PositionY { get; set; }
        public double? Width { get; set; }
        public double? Height { get; set; }
        public string? Points { get; set; } // Freehand only: "x,y x,y …"
        public string Text { get; set; } = "";
        public string Color { get; set; } = "";
        public AnnotationTextStyleResource? TextStyle { get; set; } // text-bearing kinds only (ADR 0542)
    }

    public class UpdateAnnotationRequest
    {
        public int PageIndex { get; set; }
        public double PositionX { get; set; }
        public double PositionY { get; set; }
        public double? Width { get; set; }
        public double? Height { get; set; }
        public string? Points { get; set; } // Freehand only: "x,y x,y …"
        public string Text { get; set; } = "";
        public string Color { get; set; } = "";
        public AnnotationTextStyleResource? TextStyle { get; set; } // text-bearing kinds only (ADR 0542)
    }

    private record AnnotationRow(
        Guid Id, int PageIndex, AnnotationKind Kind, double PositionX, double PositionY, double? Width, double? Height,
        string? Points, string Text, string Color, AnnotationTextStyle? TextStyle,
        Guid? CreatedByUserId, Guid? CreatedByServiceAccountId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
        Guid ConcurrencyToken, string? AuthorName);

    // A version's notes aren't paginated — a page's sticky notes are a small bounded set (like the masks list
    // or assignable-reviewers). Ordered by page then creation for a stable client render.
    [HttpGet]
    public async Task<IActionResult> List(Guid documentId, Guid versionId, CancellationToken cancellationToken)
    {
        if (!await VersionExistsAsync(documentId, versionId, cancellationToken))
        {
            return NotFound();
        }

        var rights = await GetCallerRightsAsync(documentId, cancellationToken);
        if (!rights.CanReadContent)
        {
            return Forbid();
        }

        // The owned TextStyle is projected COLUMN BY COLUMN, not as `a.TextStyle`: EF cannot materialize an owned
        // dependent into an arbitrary (non-entity) projection, and doing so throws at query time (ADR 0542). The
        // nullable casts make the seven columns readable as "absent", which is what an unstyled annotation stores.
        var rows = await _dbContext.DocumentAnnotations
            .Where(a => a.DocumentVersionId == versionId)
            .OrderBy(a => a.PageIndex).ThenBy(a => a.CreatedAt).ThenBy(a => a.Id)
            .Select(a => new
            {
                a.Id,
                a.PageIndex,
                a.Kind,
                a.PositionX,
                a.PositionY,
                a.Width,
                a.Height,
                a.Points,
                a.Text,
                a.Color,
                FontFamily = a.TextStyle!.FontFamily,
                FontSizePx = a.TextStyle!.FontSizePx,
                SizeBasis = a.TextStyle!.SizeBasis,
                Bold = (bool?)a.TextStyle!.Bold,
                Italic = (bool?)a.TextStyle!.Italic,
                Underline = (bool?)a.TextStyle!.Underline,
                Strikethrough = (bool?)a.TextStyle!.Strikethrough,
                a.CreatedByUserId,
                a.CreatedByServiceAccountId,
                a.CreatedAt,
                a.UpdatedAt,
                a.ConcurrencyToken,
                AuthorName = a.CreatedByUserId != null
                    ? _dbContext.Users.Where(u => u.Id == a.CreatedByUserId).Select(u => u.DisplayName).FirstOrDefault()
                    : _dbContext.ServiceAccounts.Where(s => s.Id == a.CreatedByServiceAccountId).Select(s => s.Name).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        return Ok(new AnnotationListResource
        {
            Annotations = rows
                .Select(r => new AnnotationRow(
                    r.Id, r.PageIndex, r.Kind, r.PositionX, r.PositionY, r.Width, r.Height, r.Points, r.Text, r.Color,
                    // All seven null = no style at all, the state every annotation was in before ADR 0542.
                    r.FontFamily is null && r.FontSizePx is null && r.SizeBasis is null && r.Bold is null
                        ? null
                        : new AnnotationTextStyle
                        {
                            FontFamily = r.FontFamily,
                            FontSizePx = r.FontSizePx,
                            SizeBasis = r.SizeBasis,
                            Bold = r.Bold ?? false,
                            Italic = r.Italic ?? false,
                            Underline = r.Underline ?? false,
                            Strikethrough = r.Strikethrough ?? false,
                        },
                    r.CreatedByUserId, r.CreatedByServiceAccountId, r.CreatedAt, r.UpdatedAt, r.ConcurrencyToken,
                    r.AuthorName))
                .Select(r => ToResource(documentId, versionId, r, rights.CanEditContent))
                .ToList(),
            CanCreate = rights.CanAnnotate,
            Links = [new Link("self", $"/api/documents/{documentId}/versions/{versionId}/annotations", "GET")],
        });
    }

    // Standing convention: every GET action gets a companion HEAD action.
    [HttpHead]
    public async Task<IActionResult> Head(Guid documentId, Guid versionId, CancellationToken cancellationToken)
    {
        if (!await VersionExistsAsync(documentId, versionId, cancellationToken))
        {
            return NotFound();
        }

        return (await GetCallerRightsAsync(documentId, cancellationToken)).CanReadContent ? NoContent() : Forbid();
    }

    [HttpPost]
    public async Task<IActionResult> Create(Guid documentId, Guid versionId, [FromBody] CreateAnnotationRequest request, CancellationToken cancellationToken)
    {
        var tenantId = await _dbContext.DocumentVersions
            .Where(v => v.Id == versionId && v.DocumentId == documentId)
            .Select(v => (Guid?)v.TenantId)
            .SingleOrDefaultAsync(cancellationToken);

        if (tenantId is null)
        {
            return NotFound();
        }

        if (!(await GetCallerRightsAsync(documentId, cancellationToken)).CanAnnotate)
        {
            return Forbid();
        }

        var kind = (AnnotationKind)request.Kind;
        var points = ValidateAnnotation(kind, request.PageIndex, request.PositionX, request.PositionY, request.Width, request.Height, request.Points, request.Text, request.Color);

        var (createdByUserId, createdByServiceAccountId) = GetCallerIdentity();
        var now = DateTimeOffset.UtcNow;

        var annotation = new DocumentAnnotation
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId.Value,
            DocumentId = documentId,
            DocumentVersionId = versionId,
            PageIndex = request.PageIndex,
            Kind = kind,
            PositionX = request.PositionX,
            PositionY = request.PositionY,
            // A note may now carry an optional size so it renders as an always-visible box (ADR "Post-it note
            // boxes"); a box shape always carries one; Freehand carries Points instead (null extent).
            Width = kind == AnnotationKind.Freehand ? null : request.Width,
            Height = kind == AnnotationKind.Freehand ? null : request.Height,
            Points = points,
            Text = request.Text.Trim(),
            Color = request.Color,
            TextStyle = ToTextStyle(kind, request.TextStyle),
            CreatedByUserId = createdByUserId,
            CreatedByServiceAccountId = createdByServiceAccountId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _dbContext.DocumentAnnotations.Add(annotation);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(AuditActions.AnnotationAdded, "Document", documentId, await DocNameAsync(documentId, cancellationToken), $"Annotation added on page {request.PageIndex + 1}", cancellationToken: cancellationToken);
        await _indexQueue.EnqueueAsync(documentId, cancellationToken); // annotation text is searchable (ADR 0526)

        var authorName = await ResolveAuthorNameAsync(createdByUserId, createdByServiceAccountId, cancellationToken);
        var resource = ToResource(documentId, versionId, ToRow(annotation, authorName), canEditContent: true);
        SetETag(annotation.ConcurrencyToken);
        return StatusCode(StatusCodes.Status201Created, resource);
    }

    // Editing a note (text / position / colour) is author-only — a CanEditContent holder can delete but not
    // rewrite someone else's note. Requires If-Match.
    [HttpPut("{annotationId:guid}")]
    public async Task<IActionResult> Update(Guid documentId, Guid versionId, Guid annotationId, [FromBody] UpdateAnnotationRequest request, CancellationToken cancellationToken)
    {
        var annotation = await _dbContext.DocumentAnnotations
            .SingleOrDefaultAsync(a => a.Id == annotationId && a.DocumentVersionId == versionId && a.DocumentId == documentId, cancellationToken);

        if (annotation is null)
        {
            return NotFound();
        }

        var rights = await GetCallerRightsAsync(documentId, cancellationToken);
        if (!rights.CanAnnotate)
        {
            return Forbid();
        }

        if (!IsAuthor(annotation))
        {
            throw new NotAnnotationAuthorException();
        }

        // Kind is fixed at creation — an edit moves/resizes/re-colours/relabels, keeping the same kind.
        var points = ValidateAnnotation(annotation.Kind, request.PageIndex, request.PositionX, request.PositionY, request.Width, request.Height, request.Points, request.Text, request.Color);

        var ifMatch = RequireIfMatch();

        annotation.PageIndex = request.PageIndex;
        annotation.PositionX = request.PositionX;
        annotation.PositionY = request.PositionY;
        annotation.Width = annotation.Kind == AnnotationKind.Freehand ? null : request.Width;
        annotation.Height = annotation.Kind == AnnotationKind.Freehand ? null : request.Height;
        annotation.Points = points;
        annotation.Text = request.Text.Trim();
        annotation.Color = request.Color;
        annotation.TextStyle = ToTextStyle(annotation.Kind, request.TextStyle);
        annotation.UpdatedAt = DateTimeOffset.UtcNow;

        _dbContext.Entry(annotation).Property(a => a.ConcurrencyToken).OriginalValue = ifMatch;

        await SaveWithConcurrencyAsync(cancellationToken);

        await _audit.RecordAsync(AuditActions.AnnotationEdited, "Document", documentId, await DocNameAsync(documentId, cancellationToken), "Annotation edited", cancellationToken: cancellationToken);
        await _indexQueue.EnqueueAsync(documentId, cancellationToken); // annotation text is searchable (ADR 0526)

        var authorName = await ResolveAuthorNameAsync(annotation.CreatedByUserId, annotation.CreatedByServiceAccountId, cancellationToken);
        SetETag(annotation.ConcurrencyToken);
        return Ok(ToResource(documentId, versionId, ToRow(annotation, authorName), rights.CanEditContent));
    }

    // Deleting a note is allowed for the author OR a CanEditContent holder (a tenant admin gets CanEditContent
    // via the ACL bypass). Requires If-Match.
    [HttpDelete("{annotationId:guid}")]
    public async Task<IActionResult> Delete(Guid documentId, Guid versionId, Guid annotationId, CancellationToken cancellationToken)
    {
        var annotation = await _dbContext.DocumentAnnotations
            .SingleOrDefaultAsync(a => a.Id == annotationId && a.DocumentVersionId == versionId && a.DocumentId == documentId, cancellationToken);

        if (annotation is null)
        {
            return NotFound();
        }

        var rights = await GetCallerRightsAsync(documentId, cancellationToken);
        if (!rights.CanReadContent)
        {
            return Forbid();
        }

        if (!IsAuthor(annotation) && !rights.CanEditContent)
        {
            throw new CannotDeleteAnnotationException();
        }

        var ifMatch = RequireIfMatch();
        _dbContext.Entry(annotation).Property(a => a.ConcurrencyToken).OriginalValue = ifMatch;
        _dbContext.DocumentAnnotations.Remove(annotation);

        await SaveWithConcurrencyAsync(cancellationToken);

        await _audit.RecordAsync(AuditActions.AnnotationRemoved, "Document", documentId, await DocNameAsync(documentId, cancellationToken), "Annotation removed", cancellationToken: cancellationToken);
        await _indexQueue.EnqueueAsync(documentId, cancellationToken); // annotation text is searchable (ADR 0526)

        return NoContent();
    }

    // --- helpers -------------------------------------------------------------------------------------------

    // The audit log records the affected document by name — a small extra lookup on the low-frequency
    // annotation mutations (the controller doesn't otherwise load the document).
    private Task<string?> DocNameAsync(Guid documentId, CancellationToken cancellationToken) =>
        _dbContext.Documents.Where(d => d.Id == documentId).Select(d => d.Name).FirstOrDefaultAsync(cancellationToken);

    // Validates an annotation and returns the canonical Freehand points to store (null for every other kind).
    // Kinds (ADR 0525): Note is a point + text; Stamp/TextBox are boxes carrying a caption/text; Highlight/
    // Rectangle/Strikethrough/Arrow are boxes; Freehand is a stroke path (Points), no extent, no text.
    private string? ValidateAnnotation(AnnotationKind kind, int pageIndex, double x, double y, double? width, double? height, string? points, string text, string color)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new InvalidAnnotationKindException();
        }

        if (pageIndex < 0)
        {
            throw new InvalidAnnotationPageException();
        }

        if (x < 0 || x > 1 || y < 0 || y > 1)
        {
            throw new InvalidAnnotationPositionException();
        }

        if (!HexColorRegex().IsMatch(color))
        {
            throw new InvalidAnnotationColorException();
        }

        if (kind == AnnotationKind.Freehand)
        {
            // A stroke path of ≥ 2 normalized points; no box extent, no text.
            return CanonicalizePoints(points);
        }

        // A caption-bearing box (Stamp/TextBox) or a plain Note requires text; the other shapes don't.
        if (kind is AnnotationKind.Note or AnnotationKind.Stamp or AnnotationKind.TextBox && string.IsNullOrWhiteSpace(text))
        {
            throw new EmptyAnnotationException();
        }

        if (kind == AnnotationKind.Note)
        {
            // A note's size is optional (a legacy note has none); when present it must be a normalized extent in
            // [-1,1], matching the CK_DocumentAnnotations_Extent DB constraint (ADR "Post-it note boxes").
            if ((width is { } nw && (nw < -1 || nw > 1)) || (height is { } nh && (nh < -1 || nh > 1)))
            {
                throw new InvalidAnnotationExtentException();
            }

            return null;
        }

        // A box shape needs a normalized extent in [-1,1] that isn't degenerate (near-zero in both dimensions).
        if (width is not { } w || height is not { } h
            || w < -1 || w > 1 || h < -1 || h > 1
            || (Math.Abs(w) < 0.001 && Math.Abs(h) < 0.001))
        {
            throw new InvalidAnnotationExtentException();
        }

        return null;
    }

    // Parse a Freehand path "x,y x,y …" into ≥ 2 normalized points (each in [0,1]) and re-emit it canonically
    // (invariant culture, 4 decimals). Throws InvalidAnnotationPoints on a malformed/too-short/out-of-range path.
    private static string CanonicalizePoints(string? points)
    {
        var pairs = (points ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (pairs.Length < 2)
        {
            throw new InvalidAnnotationPointsException();
        }

        var canonical = new List<string>(pairs.Length);
        foreach (var pair in pairs)
        {
            var xy = pair.Split(',');
            if (xy.Length != 2
                || !double.TryParse(xy[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var px)
                || !double.TryParse(xy[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var py)
                || px < 0 || px > 1 || py < 0 || py > 1)
            {
                throw new InvalidAnnotationPointsException();
            }

            canonical.Add($"{px.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)},{py.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}");
        }

        return string.Join(' ', canonical);
    }

    private Guid RequireIfMatch()
    {
        if (!Request.Headers.TryGetValue("If-Match", out var ifMatchValues) || !Guid.TryParse(ifMatchValues.ToString().Trim('"'), out var token))
        {
            throw new IfMatchRequiredException();
        }

        return token;
    }

    private async Task SaveWithConcurrencyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw EtagMismatchException.ForNote();
        }
    }

    private bool IsAuthor(DocumentAnnotation annotation)
    {
        if (_currentServiceAccountAccessor.ServiceAccountId is { } serviceAccountId)
        {
            return annotation.CreatedByServiceAccountId == serviceAccountId;
        }

        return _currentUserAccessor.UserId is { } userId && annotation.CreatedByUserId == userId;
    }

    private AnnotationResource ToResource(Guid documentId, Guid versionId, AnnotationRow row, bool canEditContent)
    {
        var isAuthor = _currentServiceAccountAccessor.ServiceAccountId is { } sid
            ? row.CreatedByServiceAccountId == sid
            : _currentUserAccessor.UserId is { } uid && row.CreatedByUserId == uid;

        return new AnnotationResource
        {
            Id = row.Id,
            PageIndex = row.PageIndex,
            Kind = (int)row.Kind,
            PositionX = row.PositionX,
            PositionY = row.PositionY,
            Width = row.Width,
            Height = row.Height,
            Points = row.Points,
            Text = row.Text,
            Color = row.Color,
            TextStyle = ToStyleResource(row.TextStyle),
            AuthorName = row.AuthorName ?? "Unknown",
            CreatedAt = row.CreatedAt,
            UpdatedAt = row.UpdatedAt,
            Etag = row.ConcurrencyToken.ToString(),
            CanEdit = isAuthor,
            CanDelete = isAuthor || canEditContent,
            Links = [new Link("self", $"/api/documents/{documentId}/versions/{versionId}/annotations/{row.Id}", "PUT")],
        };
    }

    private static AnnotationRow ToRow(DocumentAnnotation a, string? authorName) => new(
        a.Id, a.PageIndex, a.Kind, a.PositionX, a.PositionY, a.Width, a.Height, a.Points, a.Text, a.Color, a.TextStyle,
        a.CreatedByUserId, a.CreatedByServiceAccountId, a.CreatedAt, a.UpdatedAt, a.ConcurrencyToken, authorName);

    private static AnnotationTextStyleResource? ToStyleResource(AnnotationTextStyle? style) =>
        style is null
            ? null
            : new AnnotationTextStyleResource
            {
                FontFamily = style.FontFamily,
                FontSizePx = style.FontSizePx,
                SizeBasis = (int?)style.SizeBasis,
                Bold = style.Bold,
                Italic = style.Italic,
                Underline = style.Underline,
                Strikethrough = style.Strikethrough,
            };

    // The largest font size that can be stored. External-system interop encodes the size in a SIGNED BYTE, so
    // anything beyond this cannot survive an export; it is also far past any plausible annotation caption.
    private const int MaxFontSizePx = 127;

    // Longer than any real font family name, and a bound on what an unauthenticated-shaped payload can push into
    // the column. Interop formats cap this far lower, but SimplArchive is not bound to any one font catalogue.
    private const int MaxFontFamilyLength = 128;

    // Request style → the domain value object. Returns null for "unstyled", which is what an omitted style and an
    // all-default style both mean — storing the latter as a row of falses would read as a deliberate choice.
    private static AnnotationTextStyle? ToTextStyle(AnnotationKind kind, AnnotationTextStyleResource? style)
    {
        if (style is null)
        {
            return null;
        }

        // Styling describes how TEXT is drawn, so only the text-bearing kinds carry it (ADR 0542). Rejecting rather
        // than silently dropping: a client that sends a style on a shape has a bug worth surfacing.
        if (kind is not (AnnotationKind.Note or AnnotationKind.Stamp or AnnotationKind.TextBox))
        {
            throw new InvalidAnnotationTextStyleException("only a Note, Stamp or TextBox can carry text styling.");
        }

        if (style.FontSizePx is { } size && (size <= 0 || size > MaxFontSizePx))
        {
            throw new InvalidAnnotationTextStyleException($"the font size must be between 1 and {MaxFontSizePx} pixels.");
        }

        if (style.SizeBasis is { } basis && !Enum.IsDefined((FontSizeBasis)basis))
        {
            throw new InvalidAnnotationTextStyleException("the size basis must be 0 (cell height) or 1 (character height).");
        }

        var fontFamily = style.FontFamily?.Trim();
        if (fontFamily is { Length: > MaxFontFamilyLength })
        {
            throw new InvalidAnnotationTextStyleException($"the font family must be at most {MaxFontFamilyLength} characters.");
        }

        var mapped = new AnnotationTextStyle
        {
            FontFamily = string.IsNullOrEmpty(fontFamily) ? null : fontFamily,
            FontSizePx = style.FontSizePx,
            SizeBasis = (FontSizeBasis?)style.SizeBasis,
            Bold = style.Bold,
            Italic = style.Italic,
            Underline = style.Underline,
            Strikethrough = style.Strikethrough,
        };

        return mapped.IsEmpty ? null : mapped;
    }

    private async Task<bool> VersionExistsAsync(Guid documentId, Guid versionId, CancellationToken cancellationToken) =>
        await _dbContext.DocumentVersions.AnyAsync(v => v.Id == versionId && v.DocumentId == documentId, cancellationToken);

    private async Task<string?> ResolveAuthorNameAsync(Guid? userId, Guid? serviceAccountId, CancellationToken cancellationToken) =>
        userId is { } uid
            ? await _dbContext.Users.Where(u => u.Id == uid).Select(u => u.DisplayName).SingleOrDefaultAsync(cancellationToken)
            : await _dbContext.ServiceAccounts.Where(s => s.Id == serviceAccountId).Select(s => s.Name).SingleOrDefaultAsync(cancellationToken);

    private Task<EffectiveRights> GetCallerRightsAsync(Guid documentId, CancellationToken cancellationToken) =>
        _access.GetCallerRightsAsync(documentId, cancellationToken);

    private (Guid? UserId, Guid? ServiceAccountId) GetCallerIdentity() => _access.GetCallerIdentity();

    private void SetETag(Guid concurrencyToken) => Response.Headers.ETag = $"\"{concurrencyToken}\"";
}
