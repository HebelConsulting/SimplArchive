using System.Globalization;
using FolkerKinzel.VCards;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Documents;

/// <summary>
/// Auto-classification of a stored <c>.vcf</c>/<c>.ics</c> into the Contact / Calendar well-known masks
/// (#564, ADR 0619) — the CalDAV/CardDAV twin of the finalizer's email classification, in its own class
/// because <see cref="DocumentFinalizer"/> is already at the size the standing rule guards.
/// </summary>
/// <remarks>
/// It runs on ANY upload of such a file, not only on a DAV write: a contact dragged into a Contact Folder
/// through the workbench must end up indistinguishable from one a phone synced there, and the typed-folder
/// containment invariant would otherwise refuse it (the document would wear Basic Entry, not Contact).
/// Parsing is best-effort — an unparseable file falls through to the finalizer's default mask rather than
/// failing the upload, exactly as a malformed .eml does.
/// </remarks>
public sealed class CalendarContactClassifier
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly IObjectStorageClient _objectStorageClient;
    private readonly ILogger<CalendarContactClassifier> _logger;

    public CalendarContactClassifier(
        SimplArchiveDbContext dbContext, IObjectStorageClient objectStorageClient, ILogger<CalendarContactClassifier> logger)
    {
        _dbContext = dbContext;
        _objectStorageClient = objectStorageClient;
        _logger = logger;
    }

    /// <summary>The extensions this classifier owns.</summary>
    public static bool Handles(string extension) => extension is ".vcf" or ".ics";

    /// <summary>
    /// Classifies the document behind <paramref name="version"/> when it is a vCard/iCalendar, returning
    /// whether it did. The caller has already established the document is still unclassified.
    /// </summary>
    public async Task<bool> TryClassifyAsync(Document document, DocumentVersion version, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(version.ObjectKey).ToLowerInvariant();
        if (!Handles(extension))
        {
            return false;
        }

        string content;
        try
        {
            await using var stream = await _objectStorageClient.GetObjectAsync(version.ObjectKey, cancellationToken);
            using var reader = new StreamReader(stream);
            content = await reader.ReadToEndAsync(cancellationToken);
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Could not read {ObjectKey} for calendar/contact classification", version.ObjectKey);
            return false;
        }

        return extension switch
        {
            ".vcf" => await ClassifyContactAsync(document, version, content, cancellationToken),
            ".ics" => await ClassifyCalendarAsync(document, version, content, cancellationToken),
            _ => false,
        };
    }

    private async Task<bool> ClassifyContactAsync(Document document, DocumentVersion version, string content, CancellationToken cancellationToken)
    {
        FolkerKinzel.VCards.VCard? card;
        try
        {
            card = Vcf.Parse(content).FirstOrDefault();
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Unparseable vCard in {ObjectKey}", version.ObjectKey);
            return false;
        }

        if (card is null)
        {
            return false;
        }

        // A vCard's UID is optional in the wild; without one the document id stands in, so the correlation key
        // a later DAV PUT matches on always exists (a client that supplies no UID simply never matches, which
        // is the correct outcome — it is asking for a new item every time).
        var contactId = card.ContactID?.Value;
        var uid = Nonempty(contactId?.String)
            ?? Nonempty(contactId?.Guid?.ToString())
            ?? Nonempty(contactId?.Uri?.ToString())
            ?? document.Id.ToString();
        var fullName = Nonempty(card.DisplayNames?.FirstOrDefault()?.Value)
            ?? Nonempty(card.NameViews?.FirstOrDefault()?.Value?.ToString());

        var values = new List<(string Field, string? Value)>
        {
            ("Contact UID", uid),
            ("Full name", fullName),
            ("Email", Nonempty(card.EMails?.FirstOrDefault()?.Value)),
            ("Phone", Nonempty(card.Phones?.FirstOrDefault()?.Value)),
            ("Organization", Nonempty(card.Organizations?.FirstOrDefault()?.Value?.Name)),
        };

        await ApplyAsync(document, WellKnownMaskIds.Contact, values, fullName, cancellationToken);
        return true;
    }

    private async Task<bool> ClassifyCalendarAsync(Document document, DocumentVersion version, string content, CancellationToken cancellationToken)
    {
        Ical.Net.Calendar? calendar;
        try
        {
            calendar = Ical.Net.Calendar.Load(content);
        }
        catch (Exception parseFailure)
        {
            _logger.LogDebug(parseFailure, "Unparseable iCalendar in {ObjectKey}", version.ObjectKey);
            return false;
        }

        var occurrence = calendar?.Events.FirstOrDefault();
        if (occurrence is null)
        {
            return false;
        }

        // RRULE stays opaque in the stored .ics (the epic's decision — no server-side expansion), so the
        // indexed Start/End are the FIRST occurrence's: enough to find and list the item, never authoritative
        // for a recurring series. The .ics itself is what a client renders.
        var start = occurrence.DtStart?.Value;
        var end = occurrence.DtEnd?.Value;

        var values = new List<(string Field, string? Value)>
        {
            ("Event UID", Nonempty(occurrence.Uid) ?? document.Id.ToString()),
            ("Start", start is { } s ? s.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null),
            ("End", end is { } endDate ? endDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null),
            ("Location", Nonempty(occurrence.Location)),
        };

        await ApplyAsync(document, WellKnownMaskIds.Calendar, values, Nonempty(occurrence.Summary), cancellationToken);

        if (start is { } startDate)
        {
            version.DocumentDate = DateOnly.FromDateTime(startDate);
        }

        return true;
    }

    private static string? Nonempty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // Assigns the mask, writes the field values that parsed, and names the document after its human title
    // (summary / display name) when the upload carried a placeholder-ish name — same spirit as an email being
    // named after its subject.
    private async Task ApplyAsync(
        Document document, Guid maskId, IReadOnlyList<(string Field, string? Value)> values, string? title, CancellationToken cancellationToken)
    {
        var maskVersionId = await FolderMask.CurrentVersionIdAsync(_dbContext, document.TenantId, maskId, cancellationToken);
        if (maskVersionId is null)
        {
            // The mask is not seeded for this tenant — leave the document unclassified rather than half-typed.
            _logger.LogWarning("Mask {MaskId} is not seeded for tenant {TenantId}; leaving {DocumentId} unclassified",
                maskId, document.TenantId, document.Id);
            return;
        }

        var fieldIdsByName = await _dbContext.FieldDefinitions
            .Where(f => f.MaskVersionId == maskVersionId)
            .Select(f => new { f.Name, f.Id })
            .ToDictionaryAsync(f => f.Name, f => f.Id, cancellationToken);

        document.MaskVersionId = maskVersionId;
        if (Nonempty(title) is { } name)
        {
            document.Name = name;
        }

        foreach (var (field, value) in values)
        {
            if (value is null || !fieldIdsByName.TryGetValue(field, out var fieldDefinitionId))
            {
                continue;
            }

            _dbContext.FieldValues.Add(new FieldValue
            {
                Id = Guid.NewGuid(),
                TenantId = document.TenantId,
                DocumentId = document.Id,
                FieldDefinitionId = fieldDefinitionId,
                Value = value,
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
