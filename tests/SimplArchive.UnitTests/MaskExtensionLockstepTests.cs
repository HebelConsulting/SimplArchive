using SimplArchive.Domain.Masks;

namespace SimplArchive.UnitTests;

// The extension→mask mapping is stored as DATA (#671) so a picker can see it, while the code that PARSES each
// format necessarily stays code — an .eml is read as mail, a .vcf as a vCard, and no table can perform that.
//
// So the two are separate by design, and separate things drift. This is the guard: what the model says claims
// an extension, and what the code actually classifies, must name the same masks. Without it the picker would
// go on hiding a mask the classifier had stopped assigning, or offering one it had started to.
//
// The same lockstep shape as RepositoryMaskLockstepTests: storing one fact twice is safe only while something
// compares the copies.
public class MaskExtensionLockstepTests
{
    // What the CODE dispatches on, read from the two places that do it:
    //   DocumentFinalizer            — ".eml" or ".msg" → eMail
    //   CalendarContactClassifier    — ".vcf" → Contact, ".ics" → Appointment
    // Stated here rather than reflected out of them, so this file is the one place to look when the answer
    // changes, and so a reader can see the claim being made.
    private static readonly Dictionary<string, Guid> ClassifiedByCode = new(StringComparer.OrdinalIgnoreCase)
    {
        [".eml"] = WellKnownMaskIds.EMail,
        [".msg"] = WellKnownMaskIds.EMail,
        [".vcf"] = WellKnownMaskIds.Contact,
        [".ics"] = WellKnownMaskIds.Appointment,
    };

    [Fact]
    public void Every_extension_the_code_classifies_is_claimed_by_the_same_mask_in_the_model()
    {
        foreach (var (extension, maskId) in ClassifiedByCode)
        {
            var claiming = WellKnownMaskIds.FileExtensions
                .Where(pair => pair.Value.Contains(extension, StringComparer.OrdinalIgnoreCase))
                .Select(pair => pair.Key)
                .ToList();

            var claimed = Assert.Single(claiming);
            Assert.True(
                claimed == maskId,
                $"{extension} is classified as {maskId} by the code but claimed by {claimed} in the model — "
                + "the picker and the classifier would disagree about it.");
        }
    }

    [Fact]
    public void Every_extension_the_model_claims_is_actually_classified()
    {
        // The other direction, and the one that rots quietly: a mapping added to the model with no code behind
        // it removes a mask from the picker — the user loses a choice — while nothing ever assigns it, so the
        // mask becomes unreachable rather than automatic.
        foreach (var (maskId, extensions) in WellKnownMaskIds.FileExtensions)
        {
            foreach (var extension in extensions)
            {
                Assert.True(
                    ClassifiedByCode.TryGetValue(extension, out var classified) && classified == maskId,
                    $"the model gives {extension} to {maskId}, but no classifier assigns it — the mask is hidden "
                    + "from the picker and never assigned automatically, so it cannot be reached at all.");
            }
        }
    }

    [Fact]
    public void An_extension_is_claimed_by_at_most_one_mask()
    {
        // What the unique index enforces per tenant, asserted here on the shipped mapping so a second claim is
        // caught at build time rather than as a seeding failure on someone's first boot.
        //
        // Note is the live example: a note is stored as .eml, the SAME extension as a mail, and the two are told
        // apart by where they are filed. Adding Note here would break this — correctly.
        var all = WellKnownMaskIds.FileExtensions.SelectMany(pair => pair.Value).ToList();

        Assert.Equal(all.Count, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void No_folder_mask_claims_an_extension()
    {
        // A folder has no file, so an extension on a folder mask is a contradiction that would make the mask
        // both un-choosable (folder) and automatic (extension) at once.
        Assert.All(
            WellKnownMaskIds.FileExtensions.Keys,
            maskId => Assert.False(
                WellKnownMaskIds.FolderMasks.Contains(maskId),
                $"{maskId} types a folder and cannot also be claimed by a file extension."));
    }
}
