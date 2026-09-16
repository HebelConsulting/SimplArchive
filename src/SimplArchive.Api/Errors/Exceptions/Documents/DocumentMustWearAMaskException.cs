using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Documents;

/// <summary>
/// A request asked to leave a document with no mask, which is no longer a state a document can be in (#1240).
/// </summary>
/// <remarks>
/// <para>
/// The API serves no <c>PATCH</c>, so a detail request states the full intended value and a null
/// <c>MaskId</c> means "no mask" rather than "leave it alone". That used to clear the mask; it is now refused,
/// because a document without a mask has no index fields and the option only ever let someone discard a
/// document's typing and gain nothing.
/// </para>
/// <para>
/// <b>Refused rather than silently defaulted.</b> Quietly substituting Basic Entry would retype the document to
/// something the caller never named — a change they did not ask for, applied without being told. A caller that
/// wants a different mask can say which; a caller that wants none is asking for something that no longer
/// exists, and deserves to hear so.
/// </para>
/// </remarks>
public sealed class DocumentMustWearAMaskException : DocumentException
{
    public DocumentMustWearAMaskException()
        : base("DOCUMENT_MUST_WEAR_A_MASK", StatusCodes.Status400BadRequest,
            "A document must wear a mask. Name the mask to assign; clearing a document's mask is not supported.")
    {
    }
}
