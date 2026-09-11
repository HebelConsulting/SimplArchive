namespace SimplArchive.ModuleAbi;

/// <summary>
/// What a module is shown when the core is about to commit a booking (ABI 0.15, core ADR 0781), and where it
/// answers whether the booking may stand.
/// </summary>
/// <remarks>
/// The same shape as <see cref="TransitionContext"/>, deliberately: a declared delegate handed the facts, the
/// archive facade and the request's service provider. A module that already writes transition handlers needs
/// no second idiom to write this one.
/// </remarks>
/// <param name="Request">The booking as the core is about to save it.</param>
/// <param name="Archive">The module's ordinary consent-gated read/write surface.</param>
/// <param name="Services">The request's service provider, for the module's own registered services.</param>
public sealed record BookingAdmissionContext(
    BookingAdmissionRequest Request,
    IModuleArchiveFacade Archive,
    IServiceProvider Services);

/// <summary>
/// The booking a module is asked to vet: its slot, every resource it claims, and who is writing it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every claim, not one at a time.</b> A training flight claims an aircraft, a student and an instructor on
/// one document (core ADR 0774), and the questions a module asks about it are questions about the WHOLE
/// booking — "is the writer an instructor on this flight?" cannot be answered from a single claim. So the
/// review happens once, with the complete set, rather than once per claim.
/// </para>
/// <para>
/// The slot is shared by every claim by construction: they are one event, and the core's invariant requires
/// them to agree, which is why it is stated once here rather than per claim.
/// </para>
/// </remarks>
/// <param name="BookingDocumentId">The <c>.ics</c> document the claims belong to.</param>
/// <param name="StartsAtUtc">The slot's start, UTC.</param>
/// <param name="EndsAtUtc">The slot's end, UTC — exclusive, as an all-day event's DTEND already is.</param>
/// <param name="Claims">Every resource this booking claims.</param>
/// <param name="IsNew">True when this is the booking's first version; false when an existing booking is being
/// edited (which core ADR 0744 makes a rebooking, and therefore something to vet again).</param>
/// <param name="WriterUserId">The interactive user writing it, when one is.</param>
/// <param name="WriterServiceAccountId">The machine principal writing it, when one is. A module's own
/// per-tenant service principal appears here when the module itself is the writer.</param>
public sealed record BookingAdmissionRequest(
    Guid BookingDocumentId,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    IReadOnlyList<BookingAdmissionClaim> Claims,
    bool IsNew,
    Guid? WriterUserId,
    Guid? WriterServiceAccountId);

/// <summary>One resource a booking claims, with the two facts a module needs to recognise it.</summary>
/// <param name="ResourceDocumentId">The resource document — an aircraft, a room, a pilot dossier.</param>
/// <param name="MaskId">The mask it wears, so a module can tell its OWN resources from anyone else's. Null
/// only for a resource wearing no mask, which no admitted write can produce today.</param>
/// <param name="IsHolding">True for the resource whose Schedule physically holds the <c>.ics</c>; false for a
/// claim expanded from an <c>ATTENDEE</c> (core ADR 0776).</param>
/// <param name="RepresentsUserId">The person this resource stands for, when its mask declared a
/// <see cref="ModuleMaskSeed.RepresentsPrincipalField"/> (ABI 0.13) and the value resolved to a user. Null for
/// a thing rather than a person — and also for a person-representing document whose address matches no user,
/// which the core logs rather than refuses.</param>
public sealed record BookingAdmissionClaim(
    Guid ResourceDocumentId,
    Guid? MaskId,
    bool IsHolding,
    Guid? RepresentsUserId);
