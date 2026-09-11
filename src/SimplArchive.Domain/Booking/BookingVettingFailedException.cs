namespace SimplArchive.Domain.Booking;

/// <summary>
/// A module that vets bookings could not be consulted, so the booking is refused (ADR 0781).
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="BookingInvariantException"/> and from a module's own
/// <c>ModuleApiException</c> refusal, because it means something different to everyone who reads it: the
/// booking was not judged, rather than judged and rejected. An administrator needs to fix a module; the
/// pilot needs to be told it is not their booking that is wrong.
/// </para>
/// <para>
/// <b>Refusing is the deliberate choice</b> (owner decision, ADR 0781). Admitting what could not be vetted
/// would be silent: nothing errors, no screen changes, and the consent rule simply stops applying until
/// somebody reads a log — the failure direction this codebase keeps paying for. The cost is accepted and is
/// real: while a module's handler is broken, bookings of every resource in that tenant are refused.
/// </para>
/// </remarks>
public sealed class BookingVettingFailedException : Exception
{
    /// <summary>Creates the refusal, naming the module that could not answer.</summary>
    public BookingVettingFailedException(string moduleId, Exception innerException)
        : base($"The {moduleId} module could not vet this booking, so it was not accepted.", innerException) =>
        ModuleId = moduleId;

    /// <summary>The module whose handler failed — named on the wire so an administrator knows where to look.</summary>
    public string ModuleId { get; }
}
