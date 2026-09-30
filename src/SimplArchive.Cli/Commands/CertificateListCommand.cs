using System.ComponentModel;
using System.Text.Json;
using SimplArchive.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SimplArchive.Cli.Commands;

public sealed class CertificateListSettings : UserSessionSettings
{
    [CommandOption("--holder")]
    [Description("Only this holder's, by e-mail. Omitted lists the whole tenant's (an administrator's view).")]
    public string? Holder { get; init; }
}

/// <summary>
/// Reports what is enrolled (#1504) — including the rows that are NOT usable and why.
/// </summary>
/// <remarks>
/// <para>
/// Expired and revoked rows are listed on purpose: seeing the revoked one is how a holder confirms a
/// revocation took, and the module's listing deliberately does not filter by usability for that reason
/// (its ADR 0005). A listing that showed only what works would be unable to answer the question people
/// actually ask, which is "why can this person not open anything?".
/// </para>
/// <para>
/// So the columns carry the ANSWERS, not just the facts: <c>usable</c> is the module's own verdict, and
/// <c>fails policy</c> is the tenant's rule this certificate would no longer satisfy (ADR 0006) — which is
/// flagged rather than enforced backwards, so a row can be enrolled and failing at the same time.
/// </para>
/// </remarks>
public sealed class CertificateListCommand(IAnsiConsole console) : AsyncCommand<CertificateListSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, CertificateListSettings settings, CancellationToken cancellationToken)
    {
        using var http = CertificateEndpoint.Client(settings);
        var api = new SimplArchiveApi(http);
        var address = await CertificateSurface.AddressAsync(new Hypermedia(api), cancellationToken);

        // The holder rides as a QUERY on the advertised href, which is following rather than composing: the
        // server owns the path, the client owns the filter (ADR 0557's boundary).
        if (settings.Holder is { Length: > 0 } holder)
        {
            address += $"?holder={Uri.EscapeDataString(holder)}";
        }

        var rows = Rows(await api.GetAsync(address, cancellationToken)).ToList();
        if (rows.Count == 0)
        {
            console.MarkupLine(settings.Holder is { Length: > 0 }
                ? $"Nothing is enrolled for {Markup.Escape(settings.Holder)}."
                : "Nothing is enrolled in this tenant.");
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("label");
        table.AddColumn("serial");
        table.AddColumn("valid until");
        table.AddColumn("state");

        foreach (var row in rows)
        {
            table.AddRow(
                Markup.Escape(row.Label),
                Markup.Escape(row.Serial.Length > 0 ? row.Serial : "—"),
                row.NotAfter?.ToString("yyyy-MM-dd") ?? "—",
                State(row));
        }

        console.Write(table);
        return 0;
    }

    /// <summary>The one column a reader acts on: what is wrong, in the order it matters.</summary>
    /// <remarks>
    /// Revoked first, because it is a decision somebody made and outranks everything else; then expiry, a
    /// fact about the certificate; then the tenant's policy, which is a rule that changed under it. Reporting
    /// them in any other order would tell a reader to fix the least important thing.
    /// </remarks>
    private static string State(Enrolment row) => row switch
    {
        { RevokedAt: { } when } => $"[red]revoked[/] {when:yyyy-MM-dd}",
        { NotAfter: { } until } when until < DateTimeOffset.UtcNow => "[red]expired[/]",
        { FailsPolicy: { Length: > 0 } why } => $"[yellow]fails policy[/]: {Markup.Escape(why)}",
        { Usable: false } => "[red]not usable[/]",
        _ => "[green]usable[/]",
    };

    private static IEnumerable<Enrolment> Rows(JsonElement listing)
    {
        if (!listing.TryGetProperty("certificates", out var rows)
            && !listing.TryGetProperty("enrolled", out rows))
        {
            yield break;
        }

        foreach (var row in rows.EnumerateArray())
        {
            yield return new Enrolment(
                Text(row, "label"),
                Text(row, "serial"),
                Moment(row, "notAfter"),
                Moment(row, "revokedAt"),
                Text(row, "failsPolicy"),
                !row.TryGetProperty("usable", out var usable) || usable.GetBoolean());
        }
    }

    private static string Text(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static DateTimeOffset? Moment(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), out var parsed)
                ? parsed
                : null;

    private sealed record Enrolment(
        string Label,
        string Serial,
        DateTimeOffset? NotAfter,
        DateTimeOffset? RevokedAt,
        string FailsPolicy,
        bool Usable);
}
