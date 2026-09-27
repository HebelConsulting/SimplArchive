namespace SimplArchive.Domain.Abstractions;

/// <summary>
/// Marks an entity as backing HTTP ETag/If-Match optimistic concurrency (see ADR: ETag / If-Match
/// optimistic concurrency). Every entity implementing this interface automatically gets ConcurrencyToken
/// configured as an EF Core concurrency token (see SimplArchiveDbContext) and regenerated to a fresh
/// value on every Added/Modified SaveChanges — never set it manually, same precedent as
/// MaskVersion.VersionNumber/IsCurrent.
/// </summary>
/// <remarks>
/// <para>
/// <b>"Never set it manually" means never from ordinary entity code, and there is exactly ONE exception: a
/// CLAIM</b> (ADR 0836). A background sweep claims its work with <c>ExecuteUpdate</c>, which never touches the
/// ChangeTracker — so the regeneration above does not run, and a claim on a tracked entity silently stops moving
/// the token. That was found in #1444 only after three sweeps had been converted, which is why it is written here
/// rather than left to be re-derived by whoever converts the fourth.
/// </para>
/// <para>
/// <b>The rule is VISIBILITY</b> (owner's decision, 2026-09-27). A claim moves the token when it changes
/// something the reader can SEE, and leaves it when it changes invisible bookkeeping:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Move it</b> — <c>Document.DeletedAt</c> in the retention sweep (#1442). The document is now in the recycle
/// bin, so a caller holding the tag from before should be told to reload rather than writing to it successfully.
/// </description></item>
/// <item><description>
/// <b>Leave it</b> — <c>WorkflowState.EscalatedAt</c>/<c>ReminderSentAt</c> and
/// <c>ModuleActivation.EscalationLevel</c>. An approver has a form open and a reminder fires underneath them; a
/// 412 there is one they can answer only by reloading and redoing the same thing, and nothing they can see
/// changed.
/// </description></item>
/// </list>
/// <para>
/// <b>"Visible" is narrower than "rendered somewhere", and the case that shows it is
/// <c>StorageQuotaService</c>.</b> It increments <c>Tenant.StorageUsedBytes</c>, which IS displayed — so the rule
/// read literally says move the token. It must not: that counter is bumped by every upload and delete in the
/// tenant, by anybody, so moving it would fail an administrator's save of the Security tab with a 412 because an
/// unrelated user uploaded a file, on a row the nine settings PUTs already share. The question is not "can the
/// reader see this field" but <b>"did what the EDITOR is editing change?"</b> — a figure drifting under them on
/// its own is not their edit colliding with anybody's.
/// </para>
/// <para>
/// <b>Neither behaviour was ever DESIGNED</b>, which is worth knowing before "restoring" anything: the old 412
/// was a side effect of using <c>SaveChanges</c>, and its disappearance was a side effect of the conversion to
/// <c>ExecuteUpdate</c>. The rule above is the first deliberate answer, not a return to an earlier intent.
/// </para>
/// <para>
/// <b>Enforced</b> by <c>ClaimTokenDecisionTests</c>: an <c>ExecuteUpdate</c> on a tracked entity must either set
/// <c>ConcurrencyToken</c> or carry the marker <c>TOKEN DELIBERATELY NOT MOVED</c> with its reason. The guard
/// cannot judge visibility — it only insists that somebody decided, which is the part that went missing.
/// </para>
/// </remarks>
public interface IConcurrencyTracked
{
    Guid ConcurrencyToken { get; set; }
}
