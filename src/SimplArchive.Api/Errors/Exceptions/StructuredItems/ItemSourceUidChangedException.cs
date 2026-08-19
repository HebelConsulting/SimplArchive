using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.StructuredItems;

/// <summary>
/// A raw save carried a different <c>UID</c> from the stored item's (#648).
/// </summary>
/// <remarks>
/// <para>
/// The UID is the correlation key every CalDAV/CardDAV client matches on. Changing it does not rename the item;
/// it makes the item a DIFFERENT one, so the next sync keeps the old copy on the phone and adds the new — a
/// duplicate the user then has to reconcile by hand, on a device that is not in front of them.
/// </para>
/// <para>
/// Refused rather than silently rewritten, because a raw editor exists precisely so the user can see and mean
/// what they wrote: quietly substituting the stored UID would be this editor lying about the one property it
/// most needs to be honest about. A raw save with NO <c>UID</c> line is not this error — absence is not a
/// change, so the stored one is kept.
/// </para>
/// </remarks>
public sealed class ItemSourceUidChangedException : StructuredItemException
{
    public ItemSourceUidChangedException()
        : base(
            "ITEM_SOURCE_UID_CHANGED",
            StatusCodes.Status409Conflict,
            "The UID identifies this item to every calendar and address-book client that syncs it. Changing it "
            + "would leave them holding a duplicate rather than the edit. Restore the original UID, or remove "
            + "the line to keep it. Nothing was saved.")
    {
    }
}
