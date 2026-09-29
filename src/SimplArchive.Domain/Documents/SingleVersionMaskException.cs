namespace SimplArchive.Domain.Documents;

/// <summary>
/// A second version was filed against a document whose mask takes exactly one (ADR 0848,
/// <see cref="Masks.WellKnownMaskIds.SingleVersionMasks"/>).
/// </summary>
/// <remarks>
/// <para>
/// Derives from <see cref="InvalidOperationException"/> so the existing boundary catches keep working — every
/// DbContext invariant has always surfaced as that type. The dedicated type exists so the Api can tell this
/// refusal apart from the others sharing it, which is the difference between "a certificate document takes one
/// version; file a new enrolment instead" and the name-conflict message those catches otherwise invent.
/// </para>
/// <para>
/// The refusal is a POLICY, and the message has to say what to do instead or it reads as a bug. A certificate
/// document is the evidence of an enrolment (module ADR 0002); replacing its content would keep that history
/// while changing what it is evidence OF. The remedy is a new document, and the old one revoked.
/// </para>
/// </remarks>
public sealed class SingleVersionMaskException(string message) : InvalidOperationException(message);
