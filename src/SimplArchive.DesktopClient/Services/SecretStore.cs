using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// Where a long-lived credential is kept between runs: the operating system's own secret store.
/// </summary>
/// <remarks>
/// <para>
/// A refresh token renews a session without the user present, which is exactly what makes it worth stealing. It
/// does not go in <c>servers.json</c> beside the window layout — that file is plaintext, syncs with a roaming
/// profile and lands in backups, and a credential there is readable by anything that can read a file.
/// </para>
/// <para>
/// <b>No new dependency.</b> Each platform is reached through the facility it already ships — Keychain via
/// <c>security</c>, Credential Manager via <c>advapi32</c>, libsecret via <c>secret-tool</c> — so nothing is
/// added to the licence gate and nothing has to be shipped alongside the app.
/// </para>
/// <para>
/// <b>Unavailable is a normal answer, not a failure.</b> A Linux box without libsecret installed is an ordinary
/// deployment of the portable archive. The store then reports itself unavailable and the session simply lives
/// in memory for the run, which is what the client did before any of this existed — the user signs in once per
/// launch rather than being handed a worse guarantee dressed as a better one.
/// </para>
/// </remarks>
public interface ISecretStore
{
    /// <summary>Whether this machine actually has a usable store.</summary>
    bool IsAvailable { get; }

    /// <summary>The secret held for <paramref name="key"/>, or null when there is none.</summary>
    string? Read(string key);

    /// <summary>Stores (or replaces) the secret for <paramref name="key"/>. False when it could not be stored.</summary>
    bool Write(string key, string secret);

    /// <summary>Forgets the secret for <paramref name="key"/>. Absent is success.</summary>
    void Delete(string key);
}

/// <summary>Picks the right store for the platform, once.</summary>
public static class SecretStores
{
    private static ISecretStore? _current;

    /// <summary>Overridable so a test can substitute an in-memory store for the real Keychain.</summary>
    public static ISecretStore? Override { get; set; }

    public static ISecretStore Current => Override ?? (_current ??= Create());

    private static ISecretStore Create()
    {
        if (OperatingSystem.IsMacOS())
        {
            return new KeychainSecretStore();
        }

        if (OperatingSystem.IsWindows())
        {
            return new WindowsCredentialStore();
        }

        return OperatingSystem.IsLinux() ? new SecretToolStore() : new UnavailableSecretStore();
    }
}

/// <summary>The answer where the platform offers nothing — everything stays in memory for the run.</summary>
public sealed class UnavailableSecretStore : ISecretStore
{
    public bool IsAvailable => false;

    public string? Read(string key) => null;

    public bool Write(string key, string secret) => false;

    public void Delete(string key)
    {
        // Nothing was ever stored.
    }
}

/// <summary>An in-memory stand-in, so tests never touch the real store.</summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _secrets = [];

    public bool IsAvailable => true;

    public string? Read(string key) => _secrets.GetValueOrDefault(key);

    public bool Write(string key, string secret)
    {
        _secrets[key] = secret;
        return true;
    }

    public void Delete(string key) => _secrets.Remove(key);
}

/// <summary>The shared plumbing for the two stores that drive a command-line tool.</summary>
/// <remarks>
/// The secret goes in on STDIN, never in the argument list: process arguments are readable by any other process
/// on the machine, so passing it as <c>-w &lt;secret&gt;</c> would publish the very thing being protected to
/// anyone running <c>ps</c>.
/// </remarks>
public abstract class ProcessSecretStore : ISecretStore
{
    protected const string Service = "SimplArchive";

    public abstract bool IsAvailable { get; }

    public abstract string? Read(string key);

    public abstract bool Write(string key, string secret);

    public abstract void Delete(string key);

    /// <summary>
    /// Wraps a secret so it survives a text transport unchanged.
    /// </summary>
    /// <remarks>
    /// Base64 of UTF-8, because the tools do not round-trip arbitrary text: macOS <c>security</c> switches to
    /// HEX output whenever the stored password is not plain ASCII, so "rt-secret-äö-123" is written correctly
    /// and read back as "72742d…313233" with nothing reporting a problem. Encoding first makes the stored value
    /// ASCII by construction, so there is never a question of which form came back. Found by round-tripping the
    /// real Keychain, not by reading the man page — a refresh token is ASCII, so this would have lain dormant
    /// until the first secret that was not.
    /// </remarks>
    protected static string Encode(string secret) => Convert.ToBase64String(Encoding.UTF8.GetBytes(secret));

    /// <summary>Unwraps what <see cref="Encode"/> stored; null when it is not what we wrote.</summary>
    protected static string? Decode(string stored)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(stored));
        }
        catch (FormatException)
        {
            // Something else wrote this item, or an older build stored it raw. Treated as absent rather than
            // handed back as gibberish that would fail later as a puzzling auth error.
            return null;
        }
    }

    protected static bool ToolExists(string tool)
    {
        try
        {
            return Run(tool, ["--version"], null, out _) || Run(tool, ["-h"], null, out _);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Runs <paramref name="tool"/>, optionally writing <paramref name="input"/> to its stdin.</summary>
    protected static bool Run(string tool, string[] arguments, string? input, out string output)
    {
        output = string.Empty;

        try
        {
            var info = new ProcessStartInfo(tool)
            {
                RedirectStandardInput = input is not null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            using var process = Process.Start(info);
            if (process is null)
            {
                return false;
            }

            if (input is not null)
            {
                process.StandardInput.Write(input);
                process.StandardInput.Close();
            }

            output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch (Exception)
        {
            // A missing tool, a sandbox refusing to spawn, a locked keychain — all mean "no store here", which
            // the caller already handles by keeping the session in memory.
            return false;
        }
    }
}

/// <summary>macOS: the login Keychain, through the <c>security</c> tool that ships with the system.</summary>
public sealed class KeychainSecretStore : ProcessSecretStore
{
    public override bool IsAvailable => OperatingSystem.IsMacOS();

    public override string? Read(string key) =>
        Run("security", ["find-generic-password", "-s", Service, "-a", key, "-w"], null, out var output)
            ? output.TrimEnd('\n', '\r') is { Length: > 0 } stored ? Decode(stored) : null
            : null;

    /// <summary>Stores the token, with the secret on STDIN rather than in the argument list.</summary>
    /// <remarks>
    /// <para>
    /// <c>-U</c> updates an existing item rather than failing on a duplicate, so a renewal replaces the old
    /// token instead of piling a second one beside it.
    /// </para>
    /// <para>
    /// <c>-w</c> is given NO value, which makes <c>security</c> read the password from stdin — and it asks
    /// TWICE, enter then retype, so the secret is written twice or the tool answers "passwords don't match" and
    /// stores nothing. Verified against the real tool rather than assumed: the obvious single write fails in a
    /// way that looks like a keychain problem rather than like a usage error.
    /// </para>
    /// </remarks>
    public override bool Write(string key, string secret) =>
        Run("security", ["add-generic-password", "-U", "-s", Service, "-a", key, "-w"],
            Encode(secret) + "\n" + Encode(secret) + "\n", out _);

    public override void Delete(string key) =>
        Run("security", ["delete-generic-password", "-s", Service, "-a", key], null, out _);
}

/// <summary>Linux: libsecret's <c>secret-tool</c>, when the distribution has it.</summary>
public sealed class SecretToolStore : ProcessSecretStore
{
    private bool? _available;

    public override bool IsAvailable => _available ??= ToolExists("secret-tool");

    public override string? Read(string key) =>
        Run("secret-tool", ["lookup", "service", Service, "account", key], null, out var output)
            ? output.TrimEnd('\n', '\r') is { Length: > 0 } stored ? Decode(stored) : null
            : null;

    public override bool Write(string key, string secret) =>
        Run("secret-tool", ["store", "--label", $"{Service} ({key})", "service", Service, "account", key], Encode(secret), out _);

    public override void Delete(string key) =>
        Run("secret-tool", ["clear", "service", Service, "account", key], null, out _);
}

/// <summary>Windows: Credential Manager, through advapi32 — no package, and no secret on a command line.</summary>
public sealed class WindowsCredentialStore : ISecretStore
{
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;

    public bool IsAvailable => OperatingSystem.IsWindows();

    public string? Read(string key)
    {
        if (!OperatingSystem.IsWindows() || !CredRead(Target(key), CredTypeGeneric, 0, out var handle))
        {
            return null;
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(handle);
            return credential.CredentialBlobSize == 0
                ? null
                : Encoding.UTF8.GetString(ReadBlob(credential));
        }
        finally
        {
            CredFree(handle);
        }
    }

    public bool Write(string key, string secret)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var bytes = Encoding.UTF8.GetBytes(secret);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = Target(key),
                CredentialBlobSize = bytes.Length,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
            };

            return CredWrite(ref credential, 0);
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }

    public void Delete(string key)
    {
        if (OperatingSystem.IsWindows())
        {
            CredDelete(Target(key), CredTypeGeneric, 0);
        }
    }

    private static string Target(string key) => $"SimplArchive:{key}";

    private static byte[] ReadBlob(Credential credential)
    {
        var bytes = new byte[credential.CredentialBlobSize];
        Marshal.Copy(credential.CredentialBlob, bytes, 0, credential.CredentialBlobSize);
        return bytes;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }

    // DllImport rather than the newer LibraryImport: the source generator LibraryImport uses emits unsafe code,
    // which would mean turning on AllowUnsafeBlocks for the WHOLE desktop project to serve three P/Invokes into
    // advapi32. A project-wide loosening of that kind should buy more than this does.
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref Credential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr buffer);
}
