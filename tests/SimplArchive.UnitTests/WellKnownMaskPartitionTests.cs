using SimplArchive.Domain.Masks;

namespace SimplArchive.UnitTests;

// FolderMasks is hand-written — folder-ness is not a property a mask carries, and it cannot be derived where
// it is needed (SaveChanges sees a folder and a just-delivered message as equally version-less). A
// hand-written list of a growing set is exactly the shape that falls behind: RepositoryExporter.IsWellKnown
// named three of eleven masks and was never updated as the other eight arrived.
//
// So the list is guarded from BOTH sides. Asserting only "every folder mask is a well-known mask" would pass
// happily while a new mask sat in neither list — and the default for an unclassified mask is the ITEM side,
// which is precisely the one that would let a folder into an ephemeral inbox.
public class WellKnownMaskPartitionTests
{
    [Fact]
    public void Every_well_known_mask_is_classified_as_a_folder_or_an_item()
    {
        var classified = WellKnownMaskIds.FolderMasks.Concat(WellKnownMaskIds.ItemMasks).ToHashSet();
        var unclassified = WellKnownMaskIds.All.Except(classified).ToList();

        Assert.True(
            unclassified.Count == 0,
            $"{unclassified.Count} well-known mask(s) are in neither FolderMasks nor ItemMasks: "
            + $"{string.Join(", ", unclassified)}. Add each to the one it belongs to — an unclassified mask "
            + "counts as an item, so a new FOLDER mask would silently become admissible inside an ephemeral "
            + "IMAP Special folder.");
    }

    [Fact]
    public void No_mask_is_both_a_folder_and_an_item()
    {
        Assert.Empty(WellKnownMaskIds.FolderMasks.Intersect(WellKnownMaskIds.ItemMasks));
    }

    [Fact]
    public void Neither_list_names_a_mask_that_no_longer_exists()
    {
        // The other direction of the same drift: a mask retired from WellKnownMaskIds leaves a stale Guid here
        // that matches nothing, and the partition test above would still pass.
        Assert.Empty(WellKnownMaskIds.FolderMasks.Except(WellKnownMaskIds.All));
        Assert.Empty(WellKnownMaskIds.ItemMasks.Except(WellKnownMaskIds.All));
    }

    [Fact]
    public void Every_no_subfolder_folder_is_itself_a_folder_mask()
    {
        // A rule naming an item mask would be inert — nothing is ever that mask's child — and would read as
        // enforcement while enforcing nothing.
        Assert.All(WellKnownMaskIds.NoSubfolderMasks, m => Assert.Contains(m.FolderMaskId, WellKnownMaskIds.FolderMasks));
    }
}
