using MimeKit;
using MimeKit.Text;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Documents;

/// <summary>
/// Creates a Note from a title and a body, as the clients' "New note" does (#564).
/// </summary>
/// <remarks>
/// <para>
/// It stores an <b>.eml</b>, and that is the whole point rather than an implementation detail. A notebook is
/// ONE folder with TWO projections — the archive tree and an IMAP mailbox (ADR "IMAP endpoint: Notes") — and
/// the mailbox serves RFC-822 messages. A note written here as plain text would be a note Apple Notes cannot
/// read, which would quietly make "one folder, two projections" false for everything the workbench created.
/// </para>
/// <para>
/// So the message carries the same headers <c>ImapWrites.AppendNoteAsync</c> reads back:
/// <c>X-Universally-Unique-Identifier</c> is the correlation key that turns a later edit from any client into
/// a new VERSION of the same note rather than a second note, and the date becomes the "Modified" field. The
/// two paths meet at the same shape on purpose — this class exists so that shape is written down once.
/// </para>
/// </remarks>
public sealed class NoteComposer
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly IObjectStorageClient _objectStorageClient;
    private readonly DocumentFinalizer _finalizer;

    public NoteComposer(
        SimplArchiveDbContext dbContext, IObjectStorageClient objectStorageClient, DocumentFinalizer finalizer)
    {
        _dbContext = dbContext;
        _objectStorageClient = objectStorageClient;
        _finalizer = finalizer;
    }

    /// <summary>Files a new note into <paramref name="folder"/>, returning the created document.</summary>
    public async Task<Document> CreateAsync(
        Document folder, Guid tenantId, Guid userId, string title, string body, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var uuid = Guid.NewGuid().ToString();

        var noteMaskVersionId = await _dbContext.MaskVersions
            .Where(v => v.MaskId == WellKnownMaskIds.Note && v.IsCurrent)
            .Select(v => v.Id)
            .SingleAsync(cancellationToken);
        var fieldIds = await _dbContext.FieldDefinitions
            .Where(f => f.MaskVersionId == noteMaskVersionId)
            .ToDictionaryAsync(f => f.Name, f => f.Id, cancellationToken);

        // Sibling names must not collide (SaveChanges enforces it) — suffix rather than refuse, since "Note"
        // is exactly the title someone will pick twice.
        var siblings = await _dbContext.Documents
            .Where(d => d.ParentId == folder.Id)
            .Select(d => d.Name)
            .ToListAsync(cancellationToken);
        var name = title;
        for (var i = 2; siblings.Contains(name, StringComparer.OrdinalIgnoreCase); i++)
        {
            name = $"{title} ({i})";
        }

        var document = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ParentId = folder.Id,
            Name = name,
            // The mask and its REQUIRED UUID field land in the SAME save: required-field validation runs on
            // mask assignment (ADR 0176), so splitting them would refuse the note it is creating.
            MaskVersionId = noteMaskVersionId,
            CreatedByUserId = userId,
            CreatedAt = now,
            StorageFolderId = Guid.NewGuid(),
        };
        _dbContext.Documents.Add(document);
        _dbContext.FieldValues.Add(new FieldValue
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DocumentId = document.Id,
            FieldDefinitionId = fieldIds["Note UUID"],
            Value = uuid,
        });
        _dbContext.FieldValues.Add(new FieldValue
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DocumentId = document.Id,
            FieldDefinitionId = fieldIds["Modified"],
            Value = now.UtcDateTime.ToString("yyyy-MM-dd"),
        });

        var versionId = Guid.NewGuid();
        var objectKey = ObjectKeyBuilder.Build(tenantId, now, document.StorageFolderId, versionId, ".eml");
        await _objectStorageClient.PutObjectAsync(
            objectKey, new MemoryStream(Compose(title, body, uuid, now)), "message/rfc822", cancellationToken);

        // PENDING, then the shared finalizer — the same two steps every upload path takes, and the same ones
        // ImapWrites takes for a note that arrived over IMAP. It is what hashes the stored object, assigns the
        // version number and confirms it, and it is what queues indexing.
        //
        // Writing a Confirmed version directly here did not merely skip the indexing: the database refused it
        // outright (CK_DocumentVersions_Status_VersionNumber_Sha256Hash), because a confirmed version without
        // its hash is not a state the schema allows. Worth stating, since "just set the status" looks like it
        // ought to work right up until the 500.
        _dbContext.DocumentVersions.Add(new DocumentVersion
        {
            Id = versionId,
            DocumentId = document.Id,
            TenantId = tenantId,
            Status = DocumentVersionStatus.Pending,
            ObjectKey = objectKey,
            CreatedByUserId = userId,
            CreatedAt = now,
            DocumentDate = DateOnly.FromDateTime(now.UtcDateTime),
        });

        await _dbContext.SaveChangesAsync(cancellationToken);

        var version = await _dbContext.DocumentVersions.FirstAsync(v => v.Id == versionId, cancellationToken);
        await _finalizer.FinalizeAsync(version, cancellationToken);
        return document;
    }

    /// <summary>The RFC-822 message a notes client expects — see the class remarks for why it is one.</summary>
    internal static byte[] Compose(string title, string body, string uuid, DateTimeOffset when)
    {
        var message = new MimeMessage
        {
            Subject = title,
            Date = when,
            Body = new TextPart(TextFormat.Plain) { Text = body },
        };
        message.Headers.Add("X-Universally-Unique-Identifier", uuid);

        using var stream = new MemoryStream();
        message.WriteTo(stream);
        return stream.ToArray();
    }
}
