using SimplArchive.Domain.Masks;
using SimplArchive.ModuleAbi;

namespace SimplArchive.UnitTests;

// The ABI's CoreMaskIds are COPIES of the core's well-known ids (the ABI cannot reference Domain), and a
// copied GUID that drifts is a module filing into the void. This pin is what makes the copy safe (ADR 0762).
public class CoreMaskIdsLockstepTests
{
    [Fact]
    public void The_abi_core_mask_ids_match_the_well_known_table()
    {
        Assert.Equal(WellKnownMaskIds.Folder, CoreMaskIds.Folder);
        Assert.Equal(WellKnownMaskIds.BasicEntry, CoreMaskIds.BasicEntry);
        Assert.Equal(WellKnownMaskIds.Schedule, CoreMaskIds.Schedule);
    }
}
