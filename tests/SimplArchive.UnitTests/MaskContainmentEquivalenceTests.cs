using SimplArchive.Domain.Masks;

namespace SimplArchive.UnitTests;

// Containment is becoming DATA (#673). Before the invariant every typed folder depends on is moved onto the new
// tables, something has to prove the tables say the same thing the static rules say — and "the existing tests
// still pass" is a weaker claim than it sounds, because those tests assert the cases somebody thought to write.
//
// This asserts the whole space: every (parent mask, child mask) pair over every well-known mask, plus the
// no-parent case. Both readings are spelled out side by side below, so a divergence names the pair rather than
// showing up months later as something filed where it should not be.
//
// It is a SEQUENCING test, not a permanent one in its current form: once EnforceTypedFolderContainmentAsync
// reads the model, the static side becomes the historical answer and this becomes the proof the port was
// faithful. Deleting the static tables deletes this with them.
public class MaskContainmentEquivalenceTests
{
    // Every well-known mask against every other, and against "no parent" — a root, or a document whose parent
    // has no mask yet. That last case is not a curiosity: a repository created before it was stamped, and a
    // folder mid-heal, both look exactly like it.
    public static TheoryData<Guid, Guid?> AllPairs()
    {
        var data = new TheoryData<Guid, Guid?>();
        foreach (var child in WellKnownMaskIds.All)
        {
            data.Add(child, null);
            foreach (var parent in WellKnownMaskIds.All)
            {
                data.Add(child, parent);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void The_model_refuses_exactly_what_the_static_rules_refuse(Guid childMaskId, Guid? parentMaskId)
    {
        var byRules = StaticVerdict(childMaskId, parentMaskId);
        var byModel = ModelVerdict(childMaskId, parentMaskId);

        Assert.True(
            byRules == byModel,
            $"child {Name(childMaskId)} under parent {(parentMaskId is { } p ? Name(p) : "<none>")}: "
            + $"the static rules say {byRules}, the model says {byModel}.");
    }

    [Fact]
    public void The_sweep_sees_both_answers()
    {
        // An equivalence test between two agreeing functions is also passed by two functions that agree on
        // "always allow". So: the space must contain refusals as well as admissions, or the theory above proves
        // nothing at all. (The ratchet-guard lesson — a guard sees only the shape its author gave it.)
        var verdicts = AllPairs().Select(row => ModelVerdict((Guid)row[0]!, (Guid?)row[1])).ToList();

        Assert.Contains(true, verdicts);
        Assert.Contains(false, verdicts);
    }

    [Fact]
    public void Every_static_containment_rule_is_represented_in_the_model()
    {
        // The sweep above compares VERDICTS, which two differently-wrong tables could still agree on if a rule
        // were simply absent from both readings. This asserts the model's four facts are non-empty in each
        // dimension, so a projection that silently produced nothing cannot pass as agreement.
        Assert.NotEmpty(WellKnownMaskIds.ExclusiveFolderMasks);
        Assert.NotEmpty(WellKnownMaskIds.AdmittedChildMasks);
        Assert.NotEmpty(WellKnownMaskIds.AllowedParentMasks);
        Assert.NotEmpty(WellKnownMaskIds.LeafFolderMasks);

        // And the two one-directional rules specifically, since they are the ones a child-side table cannot
        // carry and the ones I got wrong once already.
        Assert.Contains(WellKnownMaskIds.Folder, WellKnownMaskIds.AdmittedChildMasks[WellKnownMaskIds.Mailbox]);
        Assert.Contains(WellKnownMaskIds.ImapSpecial, WellKnownMaskIds.LeafFolderMasks);

        // A plain Folder must remain welcome anywhere: it is listed as a Mailbox's admitted child, and if that
        // had made it a CONSTRAINED child it would now live only in mailboxes — the catastrophe
        // AlsoAdmitPlainFolders exists to prevent, and the exact reason the directions are separate tables.
        Assert.False(WellKnownMaskIds.AllowedParentMasks.ContainsKey(WellKnownMaskIds.Folder));
        Assert.False(WellKnownMaskIds.AllowedParentMasks.ContainsKey(WellKnownMaskIds.EMail));
    }

    // How SimplArchiveDbContext.EnforceTypedFolderContainmentAsync reads the STATIC tables today, with the
    // cardinality check left out — that one stays static and is a different question (capacity, not admission).
    private static bool StaticVerdict(Guid childMaskId, Guid? parentMaskId)
    {
        var alsoTakesPlainFolders =
            WellKnownMaskIds.AlsoAdmitPlainFolders.Any(m => m.FolderMaskId == parentMaskId)
            && childMaskId == WellKnownMaskIds.Folder;

        if (!alsoTakesPlainFolders
            && WellKnownMaskIds.TypedFolderRules.FirstOrDefault(r => r.FolderMaskId == parentMaskId) is { } parentRule
            && !parentRule.Admits.Any(a => a.MaskId == childMaskId))
        {
            return false;
        }

        if (WellKnownMaskIds.AdmittingFolders.TryGetValue(childMaskId, out var admitting)
            && !admitting.Any(r => r.FolderMaskId == parentMaskId))
        {
            return false;
        }

        return !(WellKnownMaskIds.FolderMasks.Contains(childMaskId)
                 && WellKnownMaskIds.NoSubfolderMasks.Any(m => m.FolderMaskId == parentMaskId));
    }

    // The same question asked of the four facts the model stores. Note there is no "also" case: a plain Folder
    // is simply one of a Mailbox's admitted children, and it stays welcome elsewhere because it has no
    // allowed-parent rows. That is the whole reason the mode column turned out to be unnecessary.
    private static bool ModelVerdict(Guid childMaskId, Guid? parentMaskId)
    {
        var exclusive = parentMaskId is { } parent && WellKnownMaskIds.ExclusiveFolderMasks.Contains(parent);
        if (exclusive
            && !(WellKnownMaskIds.AdmittedChildMasks.TryGetValue(parentMaskId!.Value, out var admits)
                 && admits.Contains(childMaskId)))
        {
            return false;
        }

        if (WellKnownMaskIds.AllowedParentMasks.TryGetValue(childMaskId, out var allowedParents)
            && !(parentMaskId is { } p && allowedParents.Contains(p)))
        {
            return false;
        }

        return !(WellKnownMaskIds.FolderMasks.Contains(childMaskId)
                 && parentMaskId is { } leafCandidate
                 && WellKnownMaskIds.LeafFolderMasks.Contains(leafCandidate));
    }

    private static string Name(Guid maskId) =>
        typeof(WellKnownMaskIds)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.FieldType == typeof(Guid) && (Guid)f.GetValue(null)! == maskId)
            .Select(f => f.Name)
            .FirstOrDefault() ?? maskId.ToString();
}
