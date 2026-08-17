using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Intray;

// Thrown when a page operation is asked of a digitally signed document (#491). A signature covers a byte range,
// so splitting, sorting, joining or straightening all void it — silently, since the file still opens and still
// looks right, and only announces itself as broken when somebody tries to verify it, possibly years later.
//
// A conforming client never sees this: the item's pages resource advertises no split or sort for a signed
// document (ADR 0554), and both clients badge the row. It is the answer to a hand-made request, and to the one
// case a client cannot pre-empt — a signature discovered between the listing and the click.
public sealed class IntrayItemIsSignedException : IntrayException
{
    public IntrayItemIsSignedException(string name)
        : base(
            "INTRAY_ITEM_IS_SIGNED",
            StatusCodes.Status409Conflict,
            $"'{name}' carries a digital signature, and any change to its pages would void it.")
    {
    }
}
