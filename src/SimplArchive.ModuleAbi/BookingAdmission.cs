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
/// <param name="SlotChanged">True when this write MOVES an existing booking — the slot differs from the one
/// its claims held before (ABI 0.17, core ADR 0783). Always false on a booking's first version, where there
/// is no previous slot to differ from.
/// <para>
/// What it is for: a rule asking somebody's consent needs to ask again when the thing they consented to
/// changes, and NOT when it does not. Substituting one claimant for another leaves everyone else committed
/// to exactly the time they already agreed to, so re-asking them would refuse an ordinary substitution;
/// moving the booking re-commits all of them to a time nobody agreed to, so not asking would be a back door
/// — book an hour somebody offered, then quietly move it to one they did not.
/// </para>
/// </param>
public sealed record BookingAdmissionRequest(
    Guid BookingDocumentId,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    IReadOnlyList<BookingAdmissionClaim> Claims,
    bool IsNew,
    Guid? WriterUserId,
    Guid? WriterServiceAccountId,
    bool SlotChanged = false);

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
/// <param name="IsNewClaim">True when this write ADDS the claim — the resource was not on the booking
/// before (ABI 0.17, core ADR 0783). Every claim of a booking's first version is new.
/// <para>
/// The companion to <see cref="BookingAdmissionRequest.SlotChanged"/>: together they say whether anything
/// changed for THIS claimant. A claim that is neither new nor moved is a commitment already made and
/// already agreed to, and asking for it again is how withdrawal-by-substitution would be refused in the
/// ordinary case — the remaining participants are still on a flight they never left.
/// </para>
/// </param>
/// <param name="IsDropped">True when this write REMOVES the claim — the resource was on the booking and is
/// coming off it (ABI 0.18, core ADR 0784). The claim is still listed, because a module deciding whether the
/// removal may stand needs to see WHO is leaving.
/// <para>
/// Without it a module cannot tell "left the flight" from "still on it", so it cannot enforce a rule about
/// leaving — and the rule most worth enforcing is exactly that one: an instructor may hand a flight to
/// somebody else, but may not simply come off it and leave nobody. A claim already cancelled by an EARLIER
/// write is history and is not shown at all.
/// </para>
/// </param>
public sealed record BookingAdmissionClaim(
    Guid ResourceDocumentId,
    Guid? MaskId,
    bool IsHolding,
    Guid? RepresentsUserId,
    bool IsNewClaim = false,
    bool IsDropped = false);
