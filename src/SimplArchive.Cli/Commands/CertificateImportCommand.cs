using System.ComponentModel;
using System.Text.Json;
using SimplArchive.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SimplArchive.Cli.Commands;

public sealed class CertificateImportSettings : TenantSessionSettings
{
    [CommandArgument(0, "<manifest>")]
    [Description("The CA's manifest: what it issued, and what it revoked.")]
    public required string Manifest { get; init; }

    [CommandOption("--dry-run")]
    [Description("Report what would change and send nothing.")]
    public bool DryRun { get; init; }
}

/// <summary>
/// Enrols and revokes reader certificates in bulk, from a certification authority's manifest (#1501).
/// </summary>
/// <remarks>
/// <para>
/// <b>An ADMINISTRATOR's tool</b> (owner-decided): every row names a holder, which the module refuses for a
/// non-administrator. Self-provisioning is a GUI matter (#1502), not this — and the certificate this enrols
/// is the reader certificate documents are addressed to, NOT the self-service mail certificate
/// <c>saconsole me certificate register</c> writes. Two stores, and this is the one that matters for content.
/// </para>
/// <para>
/// <b>Safe to re-run, which is the property that makes it usable.</b> A certificate already enrolled answers
/// <c>409 ENCRYPTION_CERTIFICATE_ALREADY_ENROLLED</c> (#1494), and that is reported as <i>unchanged</i> rather
/// than as an error — the distinct code exists for exactly this. So an interrupted import is finished by
/// running it again, and the endpoint stays honest about the fact that it changed nothing.
/// </para>
/// <para>
/// <b>Revocation joins on the SERIAL</b>, which is the only identifier a CA speaks — it names what it revoked
/// by serial and mentions a thumbprint nowhere. The module's listing carries the serial for this reason, and
/// revoking means writing the <c>Revoked on</c> index field on the certificate document, because that field
/// is the module's only revocation interface (its ADR 0005).
/// </para>
/// </remarks>
public sealed class CertificateImportCommand(IAnsiConsole console) : AsyncCommand<CertificateImportSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, CertificateImportSettings settings, CancellationToken cancellationToken)
    {
        var manifest = CertificateManifest.Read(settings.Manifest);

        using var http = CertificateEndpoint.Client(settings);
        var api = new SimplArchiveApi(http);
        var hypermedia = new Hypermedia(api);

        // THE MODULE'S OWN REL, followed from the API root — never a composed path (ADR 0543, and the owner's
        // "Even in saconsole always follow the rels"). Its absence is a real answer: the root emits a module's
        // rels only for a tenant whose activation is live, so "not advertised" means the Encryption Module is
        // not active here, which is a different problem from anything in the manifest.
        string certificates;
        try
        {
            certificates = await hypermedia.RootHrefAsync("encryption:certificates", cancellationToken);
        }
        catch (CliException)
        {
            throw new CliException(
                "This installation does not advertise the Encryption Module's certificate surface, so the "
                + "module is not active for this tenant. File its licence first: saconsole module activate.");
        }

        if (settings.DryRun)
        {
            console.MarkupLine("[yellow]Dry run[/] — nothing will be sent.");
        }

        var enrolled = await EnrolAsync(api, certificates, manifest, settings.DryRun, cancellationToken);
        var revoked = await RevokeAsync(api, hypermedia, certificates, manifest, settings.DryRun, cancellationToken);

        console.WriteLine();
        console.MarkupLine(
            $"enrolled [green]{enrolled.Enrolled}[/], unchanged [blue]{enrolled.Unchanged}[/], "
            + $"refused [red]{enrolled.Refused}[/]  ·  "
            + $"revoked [green]{revoked.Revoked}[/], already revoked [blue]{revoked.Unchanged}[/], "
            + $"not found [red]{revoked.NotFound}[/]");

        // A NON-ZERO EXIT when anything was refused, because this runs in scripts: an import that enrolled
        // nine of ten and exited 0 is an import whose failure nobody sees. "Unchanged" is not a failure.
        return enrolled.Refused + revoked.NotFound > 0 ? 1 : 0;
    }

    private async Task<(int Enrolled, int Unchanged, int Refused)> EnrolAsync(
        SimplArchiveApi api,
        string certificates,
        CertificateManifest manifest,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var (enrolled, unchanged, refused) = (0, 0, 0);

        foreach (var issued in manifest.Issued)
        {
            if (dryRun)
            {
                console.MarkupLine($"  would enrol [blue]{Markup.Escape(issued.Holder)}[/] — "
                    + Markup.Escape(issued.Label.Length > 0 ? issued.Label : "(no label)"));
                enrolled++;
                continue;
            }

            try
            {
                await api.PostAsync(
                    certificates,
                    new { certificatePem = issued.CertificatePem, label = issued.Label, holder = issued.Holder },
                    cancellationToken);

                console.MarkupLine($"  [green]enrolled[/] {Markup.Escape(issued.Holder)}");
                enrolled++;
            }
            catch (CliException exception) when (IsAlreadyEnrolled(exception))
            {
                // UNCHANGED, not refused. #1494 gave this its own code precisely so a re-run could tell the
                // difference between "you already have this" and "this cannot be enrolled".
                console.MarkupLine($"  [blue]unchanged[/] {Markup.Escape(issued.Holder)} — already enrolled");
                unchanged++;
            }
            catch (CliException exception)
            {
                // NAMED, and the import continues. A refusal is usually about ONE certificate — a key too
                // weak, a curve the tenant does not accept, a holder who is not a user here — and stopping
                // the batch would make one bad row hold up every good one.
                console.MarkupLine($"  [red]refused[/] {Markup.Escape(issued.Holder)}: {Markup.Escape(exception.Message)}");
                refused++;
            }
        }

        return (enrolled, unchanged, refused);
    }

    private async Task<(int Revoked, int Unchanged, int NotFound)> RevokeAsync(
        SimplArchiveApi api,
        Hypermedia hypermedia,
        string certificates,
        CertificateManifest manifest,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        if (manifest.Revoked.Count == 0)
        {
            return (0, 0, 0);
        }

        // ONE read of the listing for the whole batch (ADR 0557: one read, many follows). Asking per
        // revocation would be a request per row to learn something that does not change during the run.
        var listing = await api.GetAsync(certificates, cancellationToken);
        var bySerial = Enrolments(listing);

        var (revoked, unchanged, notFound) = (0, 0, 0);

        foreach (var entry in manifest.Revoked)
        {
            var serial = CertificateManifest.NormaliseSerial(entry.Serial);
            if (!bySerial.TryGetValue(serial, out var enrolment))
            {
                // NOT an error about the CA: a certificate it revoked may simply never have been enrolled
                // here, or belong to another tenant. Reported so the operator can tell which.
                console.MarkupLine($"  [red]not found[/] serial {Markup.Escape(entry.Serial)} — nothing enrolled here carries it");
                notFound++;
                continue;
            }

            if (enrolment.AlreadyRevoked)
            {
                console.MarkupLine($"  [blue]unchanged[/] serial {Markup.Escape(entry.Serial)} — already revoked");
                unchanged++;
                continue;
            }

            if (dryRun)
            {
                console.MarkupLine($"  would revoke serial [blue]{Markup.Escape(entry.Serial)}[/] on {entry.At.ToUniversalTime():yyyy-MM-dd HH:mm:ss} UTC");
                revoked++;
                continue;
            }

            await CertificateRevocation.SetRevokedOnAsync(
                api, hypermedia, enrolment.DocumentId, entry.At, cancellationToken);
            console.MarkupLine($"  [green]revoked[/] serial {Markup.Escape(entry.Serial)} on {entry.At.ToUniversalTime():yyyy-MM-dd HH:mm:ss} UTC");
            revoked++;
        }

        return (revoked, unchanged, notFound);
    }

    private static bool IsAlreadyEnrolled(CliException exception) =>
        exception.Message.Contains("ENCRYPTION_CERTIFICATE_ALREADY_ENROLLED", StringComparison.Ordinal)
        || exception.Message.Contains("already enrolled", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, (Guid DocumentId, bool AlreadyRevoked)> Enrolments(JsonElement listing)
    {
        var bySerial = new Dictionary<string, (Guid, bool)>(StringComparer.Ordinal);
        if (!listing.TryGetProperty("certificates", out var rows) && !listing.TryGetProperty("enrolled", out rows))
        {
            return bySerial;
        }

        foreach (var row in rows.EnumerateArray())
        {
            var serial = row.TryGetProperty("serial", out var s) ? s.GetString() : null;
            if (string.IsNullOrWhiteSpace(serial) || !row.TryGetProperty("documentId", out var id))
            {
                continue;
            }

            var revoked = row.TryGetProperty("revokedAt", out var at) && at.ValueKind is not JsonValueKind.Null;

            // FIRST WINS, and a duplicate is not an error here: a serial is unique per ISSUER, so two CAs can
            // both have numbered one 01. Revoking the first is the honest best effort, and the operator sees
            // the count.
            bySerial.TryAdd(CertificateManifest.NormaliseSerial(serial), (id.GetGuid(), revoked));
        }

        return bySerial;
    }
}
