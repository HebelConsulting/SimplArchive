using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SimplArchive.Api.Documents;
using SimplArchive.Api.Errors.Exceptions.Documents;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// The document's detail as ONE resource — everything the index-data pane's pencil edits, read and written
/// together (ADRs 0278/0794/0796).
/// </summary>
/// <remarks>
/// <para>
/// The pencil commits one logical edit and the clients turned it into up to EIGHT independent writes, with no
/// transaction: each step succeeded or failed on its own, so a partial failure left a half-saved document. The
/// precondition was worse than absent — only the rename sent <c>If-Match</c>, and it obtained the tag by
/// re-reading immediately before writing, which asserts "I edited what was there five milliseconds ago" and can
/// essentially never fail.
/// </para>
/// <para>
/// So: one request, one <c>If-Match</c> — the tag from the read that filled the FORM, which is the only version
/// that detects somebody else editing while it was open — one transaction, and each side effect fired once
/// after the commit rather than once per aspect.
/// </para>
/// <para>
/// The sub-resources all stay. A script setting only a sensitivity label should not have to send the whole
/// detail, and this handler DELEGATES to the same writers they use rather than carrying a second copy of any
/// of them.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/documents/{documentId:guid}/detail")]
[Authorize]
public class DocumentDetailController(
    DocumentAccessService access,
    DocumentDetailWriter detail,
    Concurrency.DocumentVerbs documents,
    IDocumentIndexQueue queue,
    ISearchablePdfQueue searchablePdfQueue,
    IWormLockService wormLock,
    IAuditRecorder audit) : ControllerBase
{
    public class DetailFieldGroup
    {
        public Guid FieldDefinitionId { get; set; }

        public List<string> Values { get; set; } = [];
    }

    /// <summary>
    /// The full intended value of the detail. Every aspect is stated, because this is a <c>PUT</c> and not a
    /// patch (the standing no-PATCH rule): what the caller sends IS the detail, and an aspect equal to what is
    /// stored is simply not a change.
    /// </summary>
    public class SetDetailRequest
    {
        public string? Name { get; set; }

        public string? DocumentDate { get; set; }

        public string? DocumentTime { get; set; }

        public List<string> OcrLanguages { get; set; } = [];

        public Guid? SensitivityLabelId { get; set; }

        public List<string> Tags { get; set; } = [];

        public List<DetailFieldGroup> Fields { get; set; } = [];

        public Guid? MaskId { get; set; }

        // Folder-only; a document lists nothing, so it is absent from a document's detail and null means
        // "leave it alone" rather than "set it to the first value of the enum".
        public FolderContentsSortOrder? ContentsSortOrder { get; set; }

        // Confirms a duplicate mailbox-address claim (#703). In one transaction the retry is simply the same
        // request again with this set — nothing was committed by the attempt that asked the question, which is
        // a strictly better contract than the per-aspect path, where the earlier steps had already landed.
        public bool ConfirmDuplicateClaims { get; set; }
    }

    public class DetailResource : HypermediaResource
    {
        public string Name { get; set; } = string.Empty;

        public string? DocumentDate { get; set; }

        public string? DocumentTime { get; set; }

        public List<string> OcrLanguages { get; set; } = [];

        public Guid? SensitivityLabelId { get; set; }

        public string? SensitivityLabelName { get; set; }

        public List<string> Tags { get; set; } = [];

        public List<DetailFieldGroup> Fields { get; set; } = [];

        public Guid? MaskId { get; set; }

        public string? MaskName { get; set; }

        public FolderContentsSortOrder? ContentsSortOrder { get; set; }

        public bool IsFolder { get; set; }

        /// <summary>The tag to send back as <c>If-Match</c>. This read is the one a save is measured against.</summary>
        public string Etag { get; set; } = string.Empty;
    }

    [HttpGet]
    public async Task<IActionResult> Get(Guid documentId, CancellationToken cancellationToken)
    {
        if (await detail.ReadAsync(documentId, cancellationToken) is not { } stored)
        {
            return NotFound();
        }

        if (!await access.CanSeeAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        documents.EmitETag(Response, stored.Document);

        return Ok(Resource(stored));
    }

    // Standing convention: every GET action gets a companion HEAD action of its own.
    [HttpHead]
    public async Task<IActionResult> Head(Guid documentId, CancellationToken cancellationToken)
    {
        if (await detail.ReadAsync(documentId, cancellationToken) is not { } stored)
        {
            return NotFound();
        }

        if (!await access.CanSeeAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        documents.EmitETag(Response, stored.Document);

        return NoContent();
    }

    [HttpPut]
    public async Task<IActionResult> Set(Guid documentId, [FromBody] SetDetailRequest request, CancellationToken cancellationToken)
    {
        if (await detail.ReadAsync(documentId, cancellationToken) is not { } stored)
        {
            return NotFound();
        }

        if (!await access.CanEditIndexDataAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        // Required, not merely honoured. This is the pencil's save, the tag is obtainable from the GET that
        // filled the form, and a precondition the caller may omit is one the collision case slips through.
        documents.RequireIfMatch(Request);

        var applied = await ApplyGatedAsync(documentId, stored, request, cancellationToken);

        if (applied.Changed == DocumentDetailWriter.Aspect.None)
        {
            // Nothing differed from what is stored. Answer the current detail rather than writing a version
            // whose only content is a new token — a no-op save must not invalidate everybody else's ETag.
            documents.EmitETag(Response, stored.Document);

            return Ok(Resource(stored));
        }

        try
        {
            await documents.MutateAsync(Request, stored.Document, apply: () => Task.CompletedTask, cancellationToken: cancellationToken);
        }
        // The contract owns the commit, so the domain refusals SaveChanges raises have to be translated around
        // it — by the same mapping the per-aspect saves use, never a second copy of it (ADR 0672).
        catch (Exception e) when (TypedFolderSave.Translate(e) is { } translated)
        {
            throw translated;
        }
        catch (InvalidOperationException e)
        {
            throw Refusal(applied.Changed, e);
        }

        await FireSideEffectsAsync(documentId, stored, applied, cancellationToken);

        documents.EmitETag(Response, stored.Document);

        return Ok(Resource(await detail.ReadAsync(documentId, cancellationToken) ?? stored));
    }

    /// <summary>
    /// ADR 0796's gate: the union of the gates of the aspects the request actually CHANGES.
    /// </summary>
    /// <remarks>
    /// Tags are deliberately weaker than the document's other metadata — <c>CanEditIndexData</c> alone, with no
    /// legal-hold or checked-out block, because they are lightweight labels like comments. Since tags are edited
    /// inside the pencil, one blanket strict gate here would have removed the ability to tag a document under
    /// legal hold from the only surface that offers it.
    ///
    /// This is NOT the "weakest of its parts" coarsening ADR 0794 warns about: no aspect is ever written under
    /// a weaker gate than its own sub-resource applies. The gate is decided by WHAT IS BEING WRITTEN, never by
    /// what is cheapest.
    ///
    /// The checks run AFTER the apply pass because the apply pass is what determines the changed set, and it
    /// only stages — nothing is committed until the caller's single transaction, so a refusal here leaves the
    /// document exactly as it was.
    /// </remarks>
    private async Task<DocumentDetailWriter.Applied> ApplyGatedAsync(
        Guid documentId,
        DocumentDetailWriter.Snapshot stored,
        SetDetailRequest request,
        CancellationToken cancellationToken)
    {
        var applied = await detail.ApplyAsync(stored, request, cancellationToken);

        if ((applied.Changed & ~DocumentDetailWriter.Aspect.Tags) != DocumentDetailWriter.Aspect.None)
        {
            await access.EnsureNotFrozenAsync(documentId, cancellationToken);
            await access.EnsureNotCheckedOutByOtherAsync(documentId, cancellationToken);
        }

        return applied;
    }

    /// <summary>
    /// One save can trip either the index-data validation or the mask's required-field rule, and
    /// <c>SaveChanges</c> raises the same <see cref="InvalidOperationException"/> for both.
    /// </summary>
    /// <remarks>
    /// So attribute it only where the changed set makes it unambiguous, and otherwise say plainly that the save
    /// was refused, carrying the invariant's own message. Guessing between the two would produce a specific,
    /// checkable, FALSE cause — telling somebody to fill in a required field when their problem is a value that
    /// does not match its format — which this codebase has now been bitten by three times.
    /// </remarks>
    private static Exception Refusal(DocumentDetailWriter.Aspect changed, InvalidOperationException e)
    {
        var mask = (changed & DocumentDetailWriter.Aspect.Mask) != 0;
        var fields = (changed & DocumentDetailWriter.Aspect.IndexData) != 0;

        return (mask, fields) switch
        {
            (true, false) => new RequiredFieldMissingException(e.Message),
            (false, true) => new FieldValueInvalidException(e.Message),
            _ => new DetailSaveRefusedException(e.Message),
        };
    }

    /// <summary>
    /// Once each, after the commit — not once per aspect, and never before it.
    /// </summary>
    /// <remarks>
    /// A side effect fired before the commit announces something that may then roll back, which is how a search
    /// index comes to hold a version the database never had.
    ///
    /// The audit events stay PER ASPECT and keep the actions the sub-resources emit. One combined
    /// "detail updated" event would make a rename from the pencil invisible to anything searching for
    /// <c>Document.Renamed</c> while the same rename through <c>PUT .../{id}</c> stayed visible — the
    /// record-per-entrance asymmetry that already produced a real gap in this codebase's audit coverage.
    /// </remarks>
    private async Task FireSideEffectsAsync(
        Guid documentId,
        DocumentDetailWriter.Snapshot stored,
        DocumentDetailWriter.Applied applied,
        CancellationToken cancellationToken)
    {
        var name = stored.Document.Name;
        var changed = applied.Changed;

        await queue.EnqueueAsync(documentId, cancellationToken);

        // The retention anchor is the document date, and a mask can carry a retention rule — so either moving
        // makes the lock wrong. Once, even when both moved.
        if ((changed & (DocumentDetailWriter.Aspect.DocumentDate | DocumentDetailWriter.Aspect.Mask)) != 0)
        {
            await wormLock.ReconcileAsync(documentId, cancellationToken);
        }

        if ((changed & DocumentDetailWriter.Aspect.OcrLanguages) != 0 && applied.OcrSourceVersion is { } source)
        {
            await searchablePdfQueue.EnqueueAsync(documentId, source.Id, cancellationToken: cancellationToken);
        }

        if ((changed & DocumentDetailWriter.Aspect.Name) != 0)
        {
            await audit.RecordAsync(AuditActions.DocumentRenamed, "Document", documentId, name, $"Renamed to '{name}'", cancellationToken: cancellationToken);
        }

        if ((changed & DocumentDetailWriter.Aspect.DocumentDate) != 0 && applied.DocumentDate is { } date)
        {
            var stamp = applied.DocumentTime is { } t ? $"{date:yyyy-MM-dd} {t:HH:mm} UTC" : $"{date:yyyy-MM-dd}";
            await audit.RecordAsync(AuditActions.DocumentDateChanged, "Document", documentId, name, $"Document date set to {stamp}", cancellationToken: cancellationToken);
        }

        if ((changed & DocumentDetailWriter.Aspect.OcrLanguages) != 0)
        {
            var codes = applied.OcrSourceVersion?.OcrLanguages;
            await audit.RecordAsync(AuditActions.DocumentOcrLanguagesChanged, "Document", documentId, name,
                codes is null ? "OCR languages reset to tenant default" : $"OCR languages set to {codes}", cancellationToken: cancellationToken);
        }

        if ((changed & DocumentDetailWriter.Aspect.Sensitivity) != 0)
        {
            await audit.RecordAsync(AuditActions.DocumentSensitivityChanged, "Document", documentId, name, $"Sensitivity set to {applied.SensitivityLabelName ?? "None"}", cancellationToken: cancellationToken);
        }

        if ((changed & DocumentDetailWriter.Aspect.Tags) != 0)
        {
            await audit.RecordAsync(AuditActions.DocumentTagsUpdated, "Document", documentId, name,
                applied.Tags.Count == 0 ? "Tags cleared" : $"Tags: {string.Join(", ", applied.Tags)}", cancellationToken: cancellationToken);
        }

        if ((changed & DocumentDetailWriter.Aspect.IndexData) != 0)
        {
            await audit.RecordAsync(AuditActions.DocumentIndexDataUpdated, "Document", documentId, name, "Index data updated", cancellationToken: cancellationToken);
        }

        if ((changed & DocumentDetailWriter.Aspect.Mask) != 0)
        {
            await (applied.MaskName is { } maskName
                ? audit.RecordAsync(AuditActions.DocumentMaskAssigned, "Document", documentId, name, $"Mask set to '{maskName}'", cancellationToken: cancellationToken)
                : audit.RecordAsync(AuditActions.DocumentMaskCleared, "Document", documentId, name, cancellationToken: cancellationToken));
        }

        if ((changed & DocumentDetailWriter.Aspect.ContentsSortOrder) != 0)
        {
            await audit.RecordAsync(AuditActions.DocumentContentsSortOrderChanged, "Document", documentId, name, $"Contents sort order set to {stored.Document.ContentsSortOrder}", cancellationToken: cancellationToken);
        }
    }

    private DetailResource Resource(DocumentDetailWriter.Snapshot stored)
    {
        var document = stored.Document;
        var isFolder = stored.IsFolder;

        return new DetailResource
        {
            Name = document.Name,
            DocumentDate = stored.CurrentVersion?.DocumentDate.ToString("yyyy-MM-dd"),
            DocumentTime = stored.CurrentVersion?.DocumentTime?.ToString("HH\\:mm"),
            OcrLanguages = stored.OcrSourceVersion?.OcrLanguages?.Split('+', StringSplitOptions.RemoveEmptyEntries).ToList() ?? [],
            SensitivityLabelId = document.SensitivityLabelId,
            SensitivityLabelName = stored.SensitivityLabelName,
            Tags = stored.Tags,
            Fields = stored.Fields,
            MaskId = stored.MaskId,
            MaskName = stored.MaskName,
            ContentsSortOrder = isFolder ? document.ContentsSortOrder : null,
            IsFolder = isFolder,
            Etag = document.ConcurrencyToken.ToString(),
            Links =
            [
                // ONE rel for both methods on one address (ADR 0719) — GET reads the detail, PUT replaces it,
                // and the method already says which. Both need CanEditIndexData to be useful, so there is no
                // narrower right for a capability flag to carry.
                new Link("self", $"/api/documents/{document.Id}/detail", "GET"),
            ],
        };
    }
}
