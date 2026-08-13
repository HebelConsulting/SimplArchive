using Avalonia;
using Avalonia.Input;

namespace SimplArchive.DesktopClient.Services;

// Keyboard shortcuts that are the same action on every platform but not the same CHORD (#482).
//
// ⌘ on macOS, Ctrl on Windows and Linux: Ctrl+O on a Mac is a chord no Mac application uses for this.
//
// Chosen from the OPERATING SYSTEM rather than from Avalonia's HotkeyConfiguration, which would read better but
// cannot be verified: the headless platform used by every screenshot and VM-check hook reports Ctrl whatever the
// machine is, so `--shortcut-test` would print the same answer on all three and prove nothing.
//
// This is the DISPLAY chord. The handler itself accepts either modifier — see MainWindow.OnWindowKeyDown, which
// has done so for Ctrl/Cmd+P since long before this.
internal static class Shortcuts
{
    // Open the selected document in its native application.
    public static KeyGesture Open => new(Key.O, OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);
}
