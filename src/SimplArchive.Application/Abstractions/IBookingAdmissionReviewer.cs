namespace SimplArchive.Application.Abstractions;

/// <summary>
/// Asks every module active for the tenant whether a booking may stand (ADR 0781), immediately before the
/// core saves it.
/// </summary>
/// <remarks>
/// Stated in core terms rather than ABI ones so the booking path — and the test that drives it — needs no
/// module machinery: the Infrastructure implementation is what translates these facts into the ABI records
/// and invokes the module's declared handler.
/// </remarks>
public interface IBookingAdmissionReviewer
{
    /// <summary>
    /// Reviews the booking. Returns normally when it may stand; throws when it may not — the module's own
    /// refusal (carrying its code and localized message) or, when a module could not be consulted at all, a
    /// refusal naming that module.
    /// </summary>
    Task ReviewAsync(BookingAdmissionFacts facts, CancellationToken cancellationToken = default);
}

/// <summary>A booking as the core is about to save it, for review.</summary>
/// <param name="BookingDocumentId">The <c>.ics</c> document the claims belong to.</param>
/// <param name="StartsAtUtc">The slot's start, UTC.</param>
/// <param name="EndsAtUtc">The slot's end, UTC, exclusive.</param>
/// <param name="Claims">Every resource claimed — one review for the whole booking, never one per claim.</param>
/// <param name="IsNew">True on the booking's first version; false when an existing one is being rebooked.</param>
/// <param name="WriterUserId">The interactive user writing it, when one is.</param>
/// <param name="WriterServiceAccountId">The machine principal writing it, when one is.</param>
public sealed record BookingAdmissionFacts(
    Guid BookingDocumentId,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    IReadOnlyList<BookingAdmissionClaimFacts> Claims,
    bool IsNew,
    Guid? WriterUserId,
    Guid? WriterServiceAccountId);

/// <summary>One claim within a reviewed booking.</summary>
/// <param name="ResourceDocumentId">The claimed resource document.</param>
/// <param name="MaskId">The mask it wears, so a module recognises its own resources.</param>
/// <param name="IsHolding">True for the resource whose Schedule holds the document.</param>
/// <param name="RepresentsUserId">The person the resource stands for (ADR 0779), when it stands for one.</param>
public sealed record BookingAdmissionClaimFacts(
    Guid ResourceDocumentId,
    Guid? MaskId,
    bool IsHolding,
    Guid? RepresentsUserId);
