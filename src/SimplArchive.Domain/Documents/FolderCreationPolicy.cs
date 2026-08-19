using SimplArchive.Domain.Masks;

namespace SimplArchive.Domain.Documents;

/// <summary>
/// Whether a plain <see cref="WellKnownMaskIds.Folder"/> may be created inside a given folder (#634).
/// </summary>
/// <remarks>
/// <para>
/// This exists so the Api can advertise the <c>folders</c> rel only where a folder can actually be made, and
/// so both clients can gate "New subfolder" on that rel instead of each carrying a copy of the rule
/// (ADR 0543: a missing rel means <i>not available to you, here, now</i>, and the client disables the
/// affordance rather than trying and handling the refusal).
/// </para>
/// <para>
/// It answers the same question three <c>SaveChanges</c> invariants answer between them, which is a real risk
/// of drift — so <c>FolderCreationPolicyAgreementTests</c> drives an actual save for every well-known folder
/// mask and asserts the two agree. A predicate that quietly disagreed with the invariant would either hide an
/// action that works or offer one that cannot.
/// </para>
/// </remarks>
public static class FolderCreationPolicy
{
    /// <param name="parentMaskId">The mask the parent folder wears; null for an unclassified one.</param>
    /// <param name="parentIsPersonalRoot">Whether the parent is a personal space's root document.</param>
    public static bool AdmitsPlainFolder(Guid? parentMaskId, bool parentIsPersonalRoot)
    {
        // The personal space's first level holds only the folders it was provisioned with (#634). Asked of the
        // shared set rather than hard-coded, so this cannot disagree with the invariant that enforces it.
        if (parentIsPersonalRoot)
        {
            return PersonalFolders.FirstLevelMasks.Contains(WellKnownMaskIds.Folder);
        }

        if (parentMaskId is not { } maskId)
        {
            // Unclassified: an upload whose type the finalizer has not decided yet. The invariants exempt it,
            // so this does too — advertising nothing here would hide the action on a folder that accepts one.
            return true;
        }

        // An ephemeral staging folder holds messages, not folders (ADR 0634).
        if (WellKnownMaskIds.NoSubfolderMasks.Any(m => m.FolderMaskId == maskId))
        {
            return false;
        }

        // A typed folder admits only its listed masks — a Notebook holds Sections and Notes, so "New subfolder"
        // there was an action the server always refused while both clients went on offering it.
        if (WellKnownMaskIds.TypedFolderRules.FirstOrDefault(r => r.FolderMaskId == maskId) is { } rule)
        {
            return rule.Admits.Any(a => a.MaskId == WellKnownMaskIds.Folder);
        }

        return true;
    }
}
