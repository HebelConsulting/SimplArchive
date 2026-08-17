using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Documents;
using SimplArchive.Api.Errors.Exceptions.Documents;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// Creating the two things a notebook holds: sections, and notes (#564).
/// </summary>
/// <remarks>
/// Their own sub-resources rather than <c>POST children</c> with a mask name, because that is what lets the
/// clients stop guessing. The document resource advertises <c>sections</c> and <c>notes</c> ONLY on a Notebook
/// or a Section, so a client shows the affordance when the rel is there and hides it when it is not — a
/// missing rel means "not available to you, here, now" (ADR 0543). The alternative was for each client to
/// read a mask name off a row and decide for itself, which is the same rule reimplemented twice, differently.
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/documents/{documentId:guid}")]
[Authorize]
public class NotebookController : ControllerBase
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly DocumentAccessService _access;
    private readonly NoteComposer _notes;
    private readonly IAuditRecorder _audit;

    public NotebookController(
        SimplArchiveDbContext dbContext, DocumentAccessService access, NoteComposer notes, IAuditRecorder audit)
    {
        _dbContext = dbContext;
        _access = access;
        _notes = notes;
        _audit = audit;
    }

    public class CreateSectionRequest
    {
        public string Name { get; set; } = string.Empty;
    }

    public class CreateNoteRequest
    {
        public string Title { get; set; } = string.Empty;

        public string Body { get; set; } = string.Empty;
    }

    /// <summary>What a create hands back: the id, and the addresses the caller acts on it through.</summary>
    /// <remarks>
    /// A create response carrying only an id is precisely what forces the next call to be composed from it
    /// (ADR 0543) — so the new section advertises what it, in turn, can hold.
    /// </remarks>
    public class CreatedResource : HypermediaResource
    {
        public Guid Id { get; set; }
    }

    /// <summary>A section inside a notebook (or inside another section — the family nests).</summary>
    [HttpPost("sections")]
    public async Task<IActionResult> CreateSection(
        Guid documentId, [FromBody] CreateSectionRequest request, CancellationToken cancellationToken)
    {
        var folder = await RequireNotebookAsync(documentId, cancellationToken);
        if (folder is null)
        {
            return NotFound();
        }

        if (!(await _access.GetCallerRightsAsync(documentId, cancellationToken)).CanCreateSubItems)
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new SectionNameRequiredException();
        }

        var (createdByUserId, createdByServiceAccountId) = _access.GetCallerIdentity();
        var section = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = folder.TenantId,
            ParentId = documentId,
            Name = request.Name.Trim(),
            MaskVersionId = await FolderMask.CurrentVersionIdAsync(
                _dbContext, folder.TenantId, WellKnownMaskIds.NotebookSection, cancellationToken),
            CreatedByUserId = createdByUserId,
            CreatedByServiceAccountId = createdByServiceAccountId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _dbContext.Documents.Add(section);

        try
        {
            await _dbContext.SaveTranslatingContainmentAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
            throw DocumentNameConflictException.OnSameParent();
        }

        await _audit.RecordAsync(AuditActions.DocumentCreated, "Document", section.Id, section.Name, cancellationToken: cancellationToken);
        return Created($"/api/documents/{section.Id}", new CreatedResource
        {
            Id = section.Id,
            Links =
            [
                new Link("self", $"/api/documents/{section.Id}", "GET"),
                new Link("children", $"/api/documents/{section.Id}/children", "GET"),
                // A section holds the same two things a notebook does, so it advertises them too.
                new Link("sections", $"/api/documents/{section.Id}/sections", "POST"),
                new Link("notes", $"/api/documents/{section.Id}/notes", "POST"),
            ],
        });
    }

    /// <summary>A note, stored as the .eml a notes client expects — see <see cref="NoteComposer"/>.</summary>
    [HttpPost("notes")]
    public async Task<IActionResult> CreateNote(
        Guid documentId, [FromBody] CreateNoteRequest request, CancellationToken cancellationToken)
    {
        var folder = await RequireNotebookAsync(documentId, cancellationToken);
        if (folder is null)
        {
            return NotFound();
        }

        if (!(await _access.GetCallerRightsAsync(documentId, cancellationToken)).CanCreateSubItems)
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(request.Title))
        {
            throw new NoteTitleRequiredException();
        }

        var (createdByUserId, _) = _access.GetCallerIdentity();
        if (createdByUserId is not { } userId)
        {
            // A note belongs to a person: it carries their UUID correlation and shows up in their notes client.
            return Forbid();
        }

        var note = await _notes.CreateAsync(
            folder, folder.TenantId, userId, request.Title.Trim(), request.Body ?? string.Empty, cancellationToken);

        await _audit.RecordAsync(AuditActions.DocumentCreated, "Document", note.Id, note.Name, cancellationToken: cancellationToken);
        return Created($"/api/documents/{note.Id}", new CreatedResource
        {
            Id = note.Id,
            Links = [new Link("self", $"/api/documents/{note.Id}", "GET")],
        });
    }

    /// <summary>The folder, when it is one a notebook family admits children into; else null.</summary>
    private async Task<Document?> RequireNotebookAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var folder = await _dbContext.Documents.FirstOrDefaultAsync(d => d.Id == documentId, cancellationToken);
        if (folder?.MaskVersionId is not { } maskVersionId)
        {
            return null;
        }

        var maskId = await _dbContext.MaskVersions
            .Where(v => v.Id == maskVersionId)
            .Select(v => (Guid?)v.MaskId)
            .SingleOrDefaultAsync(cancellationToken);

        // NotFound rather than a refusal: these sub-resources do not EXIST on an ordinary folder, which is the
        // same thing the absent rel says. A 403 would imply the caller might be granted them.
        return maskId == WellKnownMaskIds.Notebook || maskId == WellKnownMaskIds.NotebookSection ? folder : null;
    }
}
