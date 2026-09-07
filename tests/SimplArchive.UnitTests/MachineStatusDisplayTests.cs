using SimplArchive.Presentation;

namespace SimplArchive.UnitTests;

// The shared status-name split (#1062, ADR 0650): both clients render "PassengerCurrent" as
// "Passenger current" — identically, because the rule lives once in Presentation.
public class MachineStatusDisplayTests
{
    [Theory]
    [InlineData("PassengerCurrent", "Passenger current")]
    [InlineData("NightPassengerCurrent", "Night passenger current")]
    [InlineData("WindowOk", "Window ok")]
    [InlineData("Lapsed", "Lapsed")]
    [InlineData("CheckInOK", "Check in OK")] // an acronym run survives intact
    [InlineData("", "")]
    public void The_code_shaped_name_reads_as_prose(string name, string expected) =>
        Assert.Equal(expected, MachineStatusDisplay.Pretty(name));
}
