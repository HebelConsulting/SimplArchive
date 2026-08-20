using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.Masks;

/// <summary>
/// A file extension that identifies a mask — what an upload is classified as, and what a picker may offer.
/// </summary>
/// <remarks>
/// <para>
/// The mapping existed before this, scattered: <c>CalendarContactClassifier.Handles</c> knew about
/// <c>.vcf</c>/<c>.ics</c>, <c>DocumentFinalizer</c> knew about <c>.eml</c>/<c>.msg</c>, and neither was
/// visible to the endpoint that lists masks. So the picker offered masks the containment rules would refuse,
/// and the only way to find out was to save and read the error (#580).
/// </para>
/// <para>
/// <b>At most one mask per extension per tenant</b>, enforced by a unique index rather than by a rule in code.
/// That is what makes the picker's answer and the classifier's answer the same answer: with two masks claiming
/// <c>.pdf</c>, automatic classification would need a tie-break that does not exist, so the ambiguity is made
/// unrepresentable instead of arbitrated.
/// </para>
/// <para>
/// <b>Note is deliberately absent.</b> A note is stored as <c>.eml</c> — the same extension as a mail — and the
/// two are told apart by WHERE they are filed, not by their bytes. So <c>.eml</c> maps to <c>eMail</c> and the
/// Note mask is assigned by the composer that writes notes, never by extension. Mapping both would have made
/// the unique index unsatisfiable, which is the constraint doing its job: it caught the one real collision in
/// the mask family before it was written.
/// </para>
/// </remarks>
public class MaskFileExtension : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid MaskId { get; set; }

    /// <summary>Stored with its dot and lower-cased (<c>.eml</c>), so comparison never depends on the caller.</summary>
    public required string Extension { get; set; }
}
