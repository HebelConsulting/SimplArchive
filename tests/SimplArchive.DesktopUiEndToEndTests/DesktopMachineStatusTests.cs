using System.Text.Json;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopUiEndToEndTests;

// The Status section's plumbing (#1062): the parse of the resource's machineStatuses, and the VM rows the
// pane binds — pretty names via the SHARED split (ADR 0650), diagnoses only under unmet statuses, and the
// rebuild-and-clear rule (ADR 0559: a status inherited from the previous subject is a claim about the
// wrong document).
public class DesktopMachineStatusTests
{
    [Fact]
    public void The_resource_statuses_parse_with_their_diagnoses()
    {
        var json = JsonSerializer.SerializeToElement(new
        {
            machineStatuses = new object[]
            {
                new { machineId = "fs-pilot", name = "PassengerCurrent", satisfied = false, failures = new object[]
                    { new { code = "fs.not-passenger-current", value = "1 landing in the last 90 days", text = "1 landing in the last 90 days — 3 are required (EASA FCL.060)." } } },
                new { machineId = "fs-pilot", name = "NightPassengerCurrent", satisfied = true, failures = new object[0] },
            },
        });

        var statuses = DocumentsClient.ParseMachineStatuses(json);

        Assert.Equal(2, statuses.Count);
        Assert.False(statuses[0].Satisfied);
        Assert.Equal("1 landing in the last 90 days — 3 are required (EASA FCL.060).", Assert.Single(statuses[0].Failures));
        Assert.True(statuses[1].Satisfied);
        Assert.Empty(statuses[1].Failures);
    }

    [Fact]
    public void A_resource_without_statuses_parses_empty()
    {
        Assert.Empty(DocumentsClient.ParseMachineStatuses(JsonSerializer.SerializeToElement(new { links = new object[0] })));
    }

    [Fact]
    public void The_rows_carry_pretty_names_and_clear_with_the_subject()
    {
        var vm = new MainWindowViewModel();

        vm.SetDetailMachineStatuses(
        [
            new DocumentsClient.MachineStatusInfo("PassengerCurrent", false, ["needs 3 landings"]),
            new DocumentsClient.MachineStatusInfo("WindowOk", true, []),
        ]);

        Assert.True(vm.HasDetailMachineStatuses);
        Assert.Equal("Passenger current", vm.DetailMachineStatuses[0].DisplayName);
        Assert.Equal("Window ok", vm.DetailMachineStatuses[1].DisplayName);

        vm.SetDetailMachineStatuses(null);
        Assert.False(vm.HasDetailMachineStatuses);
        Assert.Empty(vm.DetailMachineStatuses);
    }
}
