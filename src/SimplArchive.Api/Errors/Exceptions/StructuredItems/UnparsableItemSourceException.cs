using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.StructuredItems;

/// <summary>
/// The raw text handed to a source save is not the format it claims to be (#648).
/// </summary>
/// <remarks>
/// A raw save REPLACES the stored item rather than merging into it, which is the whole point of a raw editor —
/// so this refusal is the only thing standing between a mistyped card and an item no client can read. Refused
/// before anything is written: there is no half-saved state to explain, and the previous version is untouched.
/// </remarks>
public sealed class UnparsableItemSourceException : StructuredItemException
{
    public UnparsableItemSourceException(string expected)
        : base(
            "UNPARSABLE_ITEM_SOURCE",
            StatusCodes.Status400BadRequest,
            $"This does not look like a {expected}. Nothing was saved — the stored item is unchanged.")
    {
    }
}
