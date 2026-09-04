using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Documents;

/// <summary>
/// The index-data PUT tried to change a classifier-owned projection field (ADRs 0743/0744) — Start/End or
/// a DAV UID on an appointment, booking or contact.
/// </summary>
/// <remarks>
/// The stored bytes own these values: a pane edit would desync the projection from what every synced
/// calendar/addressbook renders (and a booking's claimed slot would not move at all), so the change is
/// refused with the field named. Resubmitting the CURRENT values passes — this PUT is a full replacement,
/// and an honest client echoes what it was shown.
/// </remarks>
public sealed class ClassifierOwnedFieldException : DocumentException
{
    public ClassifierOwnedFieldException(string fieldName)
        : base("INDEX_FIELD_CLASSIFIER_OWNED", StatusCodes.Status400BadRequest,
            $"The field '{fieldName}' is maintained from the document's own content and cannot be edited "
            + "here — change the appointment (or rebook the slot) instead.")
    {
    }
}
