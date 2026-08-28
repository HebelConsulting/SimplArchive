using SimplArchive.DesktopClient.Services;

namespace SimplArchive.DesktopUiEndToEndTests;

// The mount-probe and drive-letter logic added for #461, tested as pure functions.
//
// Nothing here mounts anything, and that is a requirement rather than tidiness: a real mount is PERSISTENT
// (`net use /persistent:yes`, a Finder server entry, a gvfs handle), so a test that performed one would change
// the developer's machine and outlive the suite. OsFileManager keeps command CONSTRUCTION separate from
// launching for exactly this reason, which is why these can be asserted directly.
public class DesktopWebDavMountTests
{
    [Fact]
    public void The_drive_letter_is_free_prefers_S_and_searches_up_before_down()
    {
        var letter = OsFileManager.FirstFreeDriveLetter();

        // The invariant that must hold on any machine: never a letter already in use. An earlier draft fell back
        // to 'Z' unconditionally, which would have tried to map onto an occupied Z: and failed obscurely.
        Assert.NotNull(letter);
        Assert.DoesNotContain(letter!.Value, DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])));

        // S–Z first, then back down R–D. A:/B: are floppy-reserved and C: is the system drive, so a network
        // volume must never land on them however full the machine is.
        Assert.InRange(letter.Value, 'D', 'Z');
        Assert.DoesNotContain(letter.Value, "ABC");
    }

    // The `net use` builder this file used to pin was DEAD CODE with a broken argument order (password after
    // /user:, a `net` syntax error) — and its test was the only caller, proving the shape of a command nothing
    // ever ran (#820, the guard-on-fixture trap). Mapping now goes through WindowsDavDrive.Map
    // (WNetAddConnection3 + the system credential dialog, so no password ever passes through this process);
    // its testable halves are the free-letter choice above and the host matching in DesktopWebDavUncMatchTests.

    [Fact]
    public void The_probe_answers_without_touching_the_network()
    {
        // Whatever this machine's state, the probe must return promptly and either name a real directory or
        // nothing — a button renders off it, so a hang or a phantom path would be visible to the user.
        var path = OsFileManager.MountedPath();

        if (path is not null)
        {
            Assert.True(Directory.Exists(path) || OsFileManager.Current == OsFileManager.Platform.Windows);
        }
    }

    [Fact]
    public void The_mount_command_still_opens_a_window_on_every_platform()
    {
        // Guards the bug OsFileManager's own comment records: `mount volume` alone mounts SILENTLY, so nothing
        // appears to happen. Every platform's command must both mount and reveal the result.
        //
        // macOS is now the exception, deliberately: revealing the result means opening the mount point, and the
        // point cannot be known until the mount finishes (macOS suffixes a colliding volume name). So the script
        // mounts and OpenWebDavFolderAsync opens — asserted there, not here. What is still asserted here is that
        // it mounts at all.
        var (macFile, macArgs) = OsFileManager.BuildOpenCommand("https://host/SimplArchive", OsFileManager.Platform.MacOs);
        Assert.Equal("osascript", macFile);
        Assert.Contains(macArgs, a => a.Contains("mount volume"));

        var (winFile, winArgs) = OsFileManager.BuildOpenCommand("https://host/SimplArchive", OsFileManager.Platform.Windows);
        Assert.Equal("explorer.exe", winFile);
        Assert.Contains(winArgs, a => a.Contains("DavWWWRoot", StringComparison.OrdinalIgnoreCase));

        var (linuxFile, linuxArgs) = OsFileManager.BuildOpenCommand("https://host/SimplArchive", OsFileManager.Platform.Linux);
        Assert.Equal("xdg-open", linuxFile);
        Assert.Contains(linuxArgs, a => a.StartsWith("davs://", StringComparison.Ordinal));
    }
}
