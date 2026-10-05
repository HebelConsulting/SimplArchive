using System.Text.Json;
using System.Text.Json.Serialization;
using SimplArchive.Cli.Infrastructure;

namespace SimplArchive.Cli.Commands;

/// <summary>
/// What a certification authority hands over: what it issued, and what it revoked (#1501).
/// </summary>
/// <remarks>
/// <para>
/// Produced by <c>caconsole</c> (CAManagement#21) and consumed here. <b>The shape is deliberately NOT shared
/// through a package.</b> The two repositories have none, and a test that reads both sides from one source
/// agrees at every version while the wire disagrees — so each side defines it and each side has a test
/// against a literal fixture of the file. If this drifts from what caconsole writes, the fixture below is
/// what says so.
/// </para>
/// <para>
/// A file purpose-built for this import rather than the CA's own operating state (owner-decided): the CA's
/// <c>ca-state.json</c> holds the CRL number and its revocation list, which is what the CA needs to run —
/// and holds no issued certificates and no e-mail addresses at all, which is what this needs.
/// </para>
/// </remarks>
public sealed class CertificateManifest
{
    /// <summary>Certificates the CA issued, each naming who holds it.</summary>
    public List<IssuedCertificate> Issued { get; set; } = [];

    /// <summary>Certificates the CA revoked, by serial — the only identifier a CA speaks.</summary>
    public List<RevokedCertificate> Revoked { get; set; } = [];

    public sealed class IssuedCertificate
    {
        /// <summary>The holder's e-mail — how SimplArchive names a user, and what a CA does not record.</summary>
        public string Holder { get; set; } = string.Empty;

        /// <summary>What the holder calls this key: "YubiKey 5C", "work laptop".</summary>
        public string Label { get; set; } = string.Empty;

        /// <summary>The certificate, PEM-encoded. May be a chain; the module resolves the leaf.</summary>
        public string CertificatePem { get; set; } = string.Empty;

        /// <summary>The serial, for the operator's own reconciliation — not used to enrol.</summary>
        public string Serial { get; set; } = string.Empty;
    }

    public sealed class RevokedCertificate
    {
        /// <summary>The serial the CA revoked. Upper-case hex, no separators.</summary>
        public string Serial { get; set; } = string.Empty;

        /// <summary>When it was revoked — stored as this instant, offset included.</summary>
        public DateTimeOffset At { get; set; }

        /// <summary>The CA's reason, carried for the operator's log. SimplArchive stores only the moment.</summary>
        public string? Reason { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        // A field the manifest gained that this tool does not know is not a reason to refuse the file: the
        // producer is a separate tool on its own release cycle, and a strict reader would make every
        // caconsole addition a breaking change here.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    /// <summary>
    /// Reads a manifest, refusing one that cannot be acted on rather than acting on half of it.
    /// </summary>
    /// <remarks>
    /// Validated as a WHOLE before anything is sent, because this is a bulk operation against real readers'
    /// access: a file whose tenth row has no holder should be fixed and re-run, not discovered after nine
    /// enrolments have happened. The refusals name the row by its index and its serial, which is what an
    /// operator has in front of them.
    /// </remarks>
    public static CertificateManifest Read(string path)
    {
        if (!File.Exists(path))
        {
            throw new CliException($"No such manifest: {path}");
        }

        CertificateManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<CertificateManifest>(File.ReadAllText(path), Json);
        }
        catch (JsonException exception)
        {
            throw new CliException($"{path} is not readable as a certificate manifest: {exception.Message}");
        }

        if (manifest is null)
        {
            throw new CliException($"{path} is empty.");
        }

        var problems = Problems(manifest).ToList();
        if (problems.Count > 0)
        {
            throw new CliException(
                $"{path} cannot be imported:{Environment.NewLine}  " + string.Join($"{Environment.NewLine}  ", problems));
        }

        return manifest;
    }

    private static IEnumerable<string> Problems(CertificateManifest manifest)
    {
        if (manifest.Issued.Count == 0 && manifest.Revoked.Count == 0)
        {
            yield return "it names neither an issued nor a revoked certificate.";
        }

        for (var i = 0; i < manifest.Issued.Count; i++)
        {
            var issued = manifest.Issued[i];
            if (string.IsNullOrWhiteSpace(issued.Holder))
            {
                yield return $"issued[{i}] ({Describe(issued.Serial)}) names no holder — "
                    + "there is nobody to enrol it for.";
            }

            if (!issued.CertificatePem.Contains("BEGIN CERTIFICATE", StringComparison.Ordinal))
            {
                yield return $"issued[{i}] ({Describe(issued.Serial)}) carries no PEM certificate.";
            }

            // THE ONE CHECK THAT MATTERS MOST, and it is checked here rather than trusted to the server: a
            // private key in a manifest is a private key in a file somebody is about to POST over the wire,
            // and the useful moment to say so is before it leaves the machine. The server refuses it too.
            if (issued.CertificatePem.Contains("PRIVATE KEY", StringComparison.Ordinal))
            {
                yield return $"issued[{i}] ({Describe(issued.Serial)}) contains a PRIVATE KEY. Only the "
                    + "public certificate may be enrolled — and a private key in a manifest should be "
                    + "destroyed, not sent.";
            }
        }

        for (var i = 0; i < manifest.Revoked.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(manifest.Revoked[i].Serial))
            {
                yield return $"revoked[{i}] names no serial, so there is nothing to find it by.";
            }
        }
    }

    private static string Describe(string serial) =>
        string.IsNullOrWhiteSpace(serial) ? "no serial" : $"serial {serial}";

    /// <summary>The serial as both sides must spell it: upper-case hex, no separators.</summary>
    /// <remarks>
    /// <para>
    /// A CA tool may print a serial as <c>23:34:cb</c>, lower case, or with a <c>0x</c>; SimplArchive stores
    /// what <c>X509Certificate2.SerialNumber</c> renders. Normalised on the way in rather than compared
    /// loosely, because a loose comparison is the kind that matches something it should not.
    /// </para>
    /// <para>
    /// <b>LEADING ZEROS ARE SIGNIFICANT AND ARE KEPT.</b> A serial is an opaque octet string rendered as hex,
    /// not a number — <c>X509Certificate2.SerialNumber</c> keeps every byte, and the test nano's is
    /// <c>00000000221971BD…</c>, four zero bytes and all. Trimming them (the reflex, and what the first draft
    /// of this did) would make that certificate's serial fail to match its own enrolment. Only a <c>0x</c>
    /// PREFIX is removed, and only from the front.
    /// </para>
    /// </remarks>
    public static string NormaliseSerial(string serial)
    {
        var trimmed = serial.Trim()
            .Replace(":", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);

        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[2..];
        }

        return trimmed.ToUpperInvariant();
    }
}
