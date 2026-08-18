using Microsoft.EntityFrameworkCore;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Documents;

/// <summary>
/// Reads a single index-field value off a document by the field's NAME, joining through the field definition.
/// </summary>
/// <remarks>
/// This exists because the obvious shortcut is wrong in a way nothing reports. A document carries one
/// <c>FieldValue</c> row per populated field — a contact has five — so selecting from <c>FieldValues</c>
/// filtered only by <c>DocumentId</c> returns an arbitrary one of them, and without an <c>ORDER BY</c> the
/// database is free to pick a different row on a different day. Asking for "the UID" that way can hand back
/// the organisation, the phone number, or the e-mail address instead.
///
/// It matters most for exactly the field the structured editors need: a UID is the correlation key a later DAV
/// sync matches on, so writing the wrong value into a saved card or appointment forks it into a duplicate on
/// the next sync — silently, and on somebody else's device.
/// </remarks>
public static class DocumentFieldQueries
{
    /// <summary>
    /// The value of the named index field on <paramref name="documentId"/>, or null when the document has no
    /// value for it. Query filters are ignored because callers already resolved the document.
    /// </summary>
    public static async Task<string?> FieldValueAsync(
        this SimplArchiveDbContext dbContext, Guid documentId, string fieldName, CancellationToken cancellationToken) =>
        await dbContext.FieldValues.IgnoreQueryFilters()
            .Where(v => v.DocumentId == documentId)
            .Join(
                dbContext.FieldDefinitions.IgnoreQueryFilters().Where(d => d.Name == fieldName),
                value => value.FieldDefinitionId,
                definition => definition.Id,
                (value, _) => value.Value)
            .FirstOrDefaultAsync(cancellationToken);
}
