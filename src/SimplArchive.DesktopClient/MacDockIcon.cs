using System.Runtime.InteropServices;

namespace SimplArchive.DesktopClient;

// Sets the macOS Dock icon at runtime via AppKit — needed because Avalonia's Window.Icon doesn't drive the
// Dock, and under `dotnet run` there's no .app bundle to carry an .icns. Best-effort Objective-C interop,
// guarded to macOS and wrapped so a failure never affects startup. See ADR "Desktop app icon".
internal static class MacDockIcon
{
    private const string ObjC = "/usr/lib/libobjc.dylib";

    [DllImport(ObjC, EntryPoint = "objc_getClass", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetClass(string name);

    [DllImport(ObjC, EntryPoint = "sel_registerName", CharSet = CharSet.Ansi)]
    private static extern IntPtr Sel(string name);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg);

    public static void TrySet(string pngPath)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var cString = IntPtr.Zero;
        try
        {
            cString = Marshal.StringToCoTaskMemUTF8(pngPath);
            var nsString = Send(GetClass("NSString"), Sel("stringWithUTF8String:"), cString);
            var image = Send(Send(GetClass("NSImage"), Sel("alloc")), Sel("initWithContentsOfFile:"), nsString);
            if (image == IntPtr.Zero)
            {
                return;
            }

            var app = Send(GetClass("NSApplication"), Sel("sharedApplication"));
            Send(app, Sel("setApplicationIconImage:"), image);
        }
        catch
        {
            // Cosmetic only — never let a Dock-icon failure break the app.
        }
        finally
        {
            if (cString != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(cString);
            }
        }
    }
}
