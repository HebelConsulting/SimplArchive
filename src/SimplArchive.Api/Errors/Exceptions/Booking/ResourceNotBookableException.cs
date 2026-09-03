using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Booking;

/// <summary>The target document's mask does not declare it bookable (ADR 0735).</summary>
/// <remarks>
/// A conforming client never hits this — the `bookings` rel is only emitted on bookable resources
/// (ADR 0543: a missing rel means "not available"), so this is the enforcer's half of that emission rule
/// (the withhold-only-when-following-would-fail principle: emitter and enforcer share one predicate).
/// </remarks>
public sealed class ResourceNotBookableException : BookingException
{
    public ResourceNotBookableException(Guid documentId)
        : base("RESOURCE_NOT_BOOKABLE", StatusCodes.Status409Conflict,
            $"Document {documentId} is not a bookable resource.")
    {
    }
}
