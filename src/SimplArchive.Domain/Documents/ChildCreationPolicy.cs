using SimplArchive.Domain.Masks;

namespace SimplArchive.Domain.Documents;

/// <summary>
/// Whether a PLAIN child — one wearing <see cref="WellKnownMaskIds.Folder"/> — may be created inside a given
/// folder (#634).
/// </summary>
/// <remarks>
/// <para>
/// "Plain child" rather than "folder", because a new folder and an UPLOADED DOCUMENT are the same create:
/// <c>DocumentChildrenController</c> stamps <see cref="WellKnownMaskIds.Folder"/> on either, and
/// <c>DocumentFinalizer</c> reclassifies to Basic Entry / eMail only once bytes arrive. So one predicate
/// answers both "may I make a folder here?" and "may I drop a file here?", for every folder in the model —
/// there is no mask for which the two differ, precisely because there is only one create.
/// </para>
/// <para>
/// That is why the rel it drives is <c>create-child</c> and not <c>folders</c>. The narrower name invited a
/// second rel for uploads, which would have been a second answer to one question — the drift this area keeps
/// producing (ADR 0637).
/// </para>
/// <para>
/// It exists so the Api advertises that rel only where the create can actually succeed, and so both clients
/// gate "New subfolder", the drop-zone and Upload on it instead of each carrying a copy of the rule
/// (ADR 0543: a missing rel means <i>not available to you, here, now</i>, and the client withholds the
/// affordance rather than offering one the server refuses).
/// </para>
/// <para>
/// It answers the same question three <c>SaveChanges</c> invariants answer between them, which is a real risk
/// of drift — so <c>ChildCreationPolicyAgreementTests</c> drives an actual save for every well-known folder
/// mask and asserts the two agree, and <c>ChildCreationRelCoverageTests</c> asserts every listing a client
/// builds a node from actually emits the rel. A predicate that quietly disagreed with the invariant would hide
/// an action that works or offer one that cannot; a listing that omits the rel does the former silently.
/// </para>
/// </remarks>
public static class ChildCreationPolicy
{
    /// <param name="parentMaskId">The mask the parent folder wears; null for an unclassified one.</param>
    /// <param name="parentIsPersonalRoot">Whether the parent is a personal space's root document.</param>
    public static bool AdmitsPlainChild(Guid? parentMaskId, bool parentIsPersonalRoot)
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

        // An ephemeral staging folder holds delivered messages under the mail key prefix, not members of the
        // repository (ADR 0634) — so neither a subfolder nor an uploaded document belongs there.
        if (WellKnownMaskIds.NoSubfolderMasks.Any(m => m.FolderMaskId == maskId))
        {
            return false;
        }

        // …unless it is one of the typed folders that also takes ordinary folders (#596) — a mailbox does, so
        // "New subfolder" and IMAP CREATE both work on it without either side special-casing mail.
        if (WellKnownMaskIds.AlsoAdmitPlainFolders.Any(m => m.FolderMaskId == maskId))
        {
            return true;
        }

        // A typed folder admits only its listed masks — a Notebook holds Sections and Notes, so "New subfolder"
        // there was an action the server always refused while both clients went on offering it. Its own creates
        // are reached by their own rels (`sections`, `notes`), which is why this asks only about the plain one.
        if (WellKnownMaskIds.TypedFolderRules.FirstOrDefault(r => r.FolderMaskId == maskId) is { } rule)
        {
            return rule.Admits.Any(a => a.MaskId == WellKnownMaskIds.Folder);
        }

        return true;
    }

    /// <summary>
    /// Whether a folder admits a TYPED item — a Contact, an Appointment, a Note — and therefore advertises that
    /// family's own create rel (#631).
    /// </summary>
    /// <remarks>
    /// The counterpart to <see cref="AdmitsPlainChild"/>, and here for the same reason: it is asked on every
    /// surface a client reads a folder from, and each surface answering it for itself is how a rel ends up on
    /// two of three. That is not hypothetical — <c>contacts</c>/<c>appointments</c> shipped on the document
    /// resource and the children listing while <c>GET /api/dav-collections</c>, the listing the Contacts and
    /// Calendar tabs actually read their collections from, advertised neither. Both clients therefore had no
    /// address to create from on the one screen the feature is for, exactly as <c>folders</c> had none on every
    /// repository root (#638).
    /// </remarks>
    /// <param name="folderMaskId">The mask the folder wears; null for an unclassified one, which admits none.</param>
    /// <param name="itemMaskId">The item mask in question, e.g. <see cref="WellKnownMaskIds.Contact"/>.</param>
    public static bool AdmitsTypedItem(Guid? folderMaskId, Guid itemMaskId) =>
        folderMaskId is { } maskId
        && WellKnownMaskIds.TypedFolderRules.FirstOrDefault(r => r.FolderMaskId == maskId) is { } rule
        && rule.Admits.Any(a => a.MaskId == itemMaskId);
}
