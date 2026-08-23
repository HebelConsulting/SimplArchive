using System.Net.Sockets;
using System.Text;

namespace SimplArchive.Api.Lmtp;

/// <summary>One LMTP conversation (RFC 2033) — the protocol surface only; delivery is <see cref="LmtpDelivery"/>.</summary>
/// <remarks>
/// <para>
/// LMTP is deliberately <b>not</b> SMTP, and the two differences are the whole reason ADR 0628 chose it. It
/// has <b>no queue of its own</b>, and after the message body it emits <b>one reply per accepted recipient</b>
/// rather than a single reply for the message. That per-recipient reply is what lets us accept mail for one
/// user and defer it for another in the same transaction, and it is the most commonly mis-implemented part of
/// the protocol — an implementation that sends one reply looks fine against a single-recipient test and
/// silently loses mail as soon as two recipients appear.
/// </para>
/// <para>
/// ADR 0626 applies: the whole exchange is answerable at Trace, and anything refused says so at Warning.
/// </para>
/// </remarks>
internal sealed class LmtpSession
{
    private readonly TcpClient _client;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly LmtpOptions _options;
    private readonly ILogger _logger;
    private readonly List<string> _recipients = [];
    private string _sender = string.Empty;
    private bool _greeted;
    private StreamWriter _writer = null!;

    internal LmtpSession(TcpClient client, IServiceScopeFactory scopeFactory, LmtpOptions options, ILogger logger)
    {
        _client = client;
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        var stream = _client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 8192, leaveOpen: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 8192, leaveOpen: true) { AutoFlush = true, NewLine = "\r\n" };
        _writer = writer;

        await ReplyAsync("220 SimplArchive LMTP ready");

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                return;
            }

            _logger.LogTrace("LMTP → {Line}", Redact(line));

            var verb = line.Split(' ', 2)[0].ToUpperInvariant();
            var rest = line.Length > verb.Length ? line[(verb.Length + 1)..].Trim() : string.Empty;

            switch (verb)
            {
                case "LHLO":
                    _greeted = true;
                    await ReplyAsync("250-SimplArchive");
                    await ReplyAsync($"250-SIZE {_options.MaxMessageBytes}");
                    await ReplyAsync("250 PIPELINING");
                    break;

                // EHLO/HELO are SMTP's greeting, not LMTP's. RFC 2033 is explicit that an LMTP server must
                // reject them — a client sending one is talking the wrong protocol to this port, and answering
                // it anyway would let a misconfigured MTA appear to work while the semantics differ.
                case "EHLO":
                case "HELO":
                    _logger.LogWarning(
                        "LMTP: refused {Verb} — this is an LMTP listener and RFC 2033 requires LHLO. The peer is "
                        + "speaking SMTP to an LMTP port and will believe it succeeded if answered; set "
                        + "Serilog:MinimumLevel:Override:SimplArchive.Api.Lmtp to Trace to see the exchange",
                        verb);
                    await ReplyAsync("500 this is LMTP; use LHLO (RFC 2033)");
                    break;

                case "MAIL":
                    if (!_greeted)
                    {
                        await ReplyAsync("503 LHLO first");
                        break;
                    }

                    _sender = ExtractPath(rest);
                    _recipients.Clear();
                    await ReplyAsync("250 sender accepted");
                    break;

                case "RCPT":
                    if (_sender.Length == 0)
                    {
                        await ReplyAsync("503 MAIL first");
                        break;
                    }

                    await RecipientAsync(ExtractPath(rest), cancellationToken);
                    break;

                case "DATA":
                    if (_recipients.Count == 0)
                    {
                        await ReplyAsync("503 no valid recipients");
                        break;
                    }

                    await DataAsync(reader, cancellationToken);
                    break;

                case "RSET":
                    _sender = string.Empty;
                    _recipients.Clear();
                    await ReplyAsync("250 reset");
                    break;

                case "NOOP":
                    await ReplyAsync("250 ok");
                    break;

                case "QUIT":
                    await ReplyAsync("221 bye");
                    return;

                default:
                    _logger.LogWarning(
                        "LMTP: refused unrecognised command {Verb}; the peer may treat this as a transient "
                        + "problem and retry for ever. Set Serilog:MinimumLevel:Override:SimplArchive.Api.Lmtp "
                        + "to Trace to see the exchange",
                        verb);
                    await ReplyAsync("500 unrecognised command");
                    break;
            }
        }
    }

    private async Task RecipientAsync(string address, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var delivery = scope.ServiceProvider.GetRequiredService<LmtpDelivery>();

        // Resolving at RCPT rather than at DATA is what lets the MTA bounce an unknown address without ever
        // transferring the body — and ADR 0628 requires 550 rather than a silent accept, so the sender learns.
        if ((await delivery.ResolveAsync(address, cancellationToken)).Count == 0)
        {
            _logger.LogWarning(
                "LMTP: refused recipient {Address} — no tenant claims its domain, no user owns its local "
                + "part, and no mailbox claims it. The MTA will bounce to the sender. Set "
                + "Serilog:MinimumLevel:Override:SimplArchive.Api.Lmtp to Trace to see the exchange",
                address);
            await ReplyAsync("550 no such recipient here");
            return;
        }

        _recipients.Add(address);
        await ReplyAsync("250 recipient accepted");
    }

    private async Task DataAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        await ReplyAsync("354 send it; end with <CRLF>.<CRLF>");

        var body = new MemoryStream();
        var tooLarge = false;
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                // The peer vanished mid-body. Nothing was stored, and no 250 was sent, so the MTA still owns
                // the mail and will retry — which is exactly the property ADR 0628 wanted from LMTP.
                _logger.LogWarning("LMTP: connection closed mid-DATA; nothing stored, the MTA retains the mail");
                return;
            }

            if (line == ".")
            {
                break;
            }

            // Dot-stuffing (RFC 5321 §4.5.2): a body line starting with '.' arrives doubled.
            var content = line.StartsWith("..", StringComparison.Ordinal) ? line[1..] : line;

            if (!tooLarge && body.Length + content.Length + 2 > _options.MaxMessageBytes)
            {
                // Keep reading to the terminator rather than hanging up: the peer is mid-transfer, and a
                // clean permanent refusal is what makes it bounce instead of retrying for ever.
                tooLarge = true;
            }

            if (!tooLarge)
            {
                var bytes = Encoding.UTF8.GetBytes(content);
                body.Write(bytes, 0, bytes.Length);
                body.WriteByte((byte)'\r');
                body.WriteByte((byte)'\n');
            }
        }

        if (tooLarge)
        {
            _logger.LogWarning(
                "LMTP: refused a message over the {Limit}-byte cap for {Count} recipient(s); the MTA will bounce "
                + "it to the sender", _options.MaxMessageBytes, _recipients.Count);
            foreach (var _ in _recipients)
            {
                await ReplyAsync("552 message too large");
            }

            _recipients.Clear();
            _sender = string.Empty;
            return;
        }

        var payload = body.ToArray();

        // THE per-recipient reply. One line per accepted recipient, in the order they were accepted — this is
        // the LMTP difference, and sending a single reply here is the bug that only appears with two
        // recipients.
        foreach (var recipient in _recipients)
        {
            using var scope = _scopeFactory.CreateScope();
            var delivery = scope.ServiceProvider.GetRequiredService<LmtpDelivery>();
            await ReplyAsync(await delivery.DeliverAsync(recipient, _sender, payload, cancellationToken));
        }

        _recipients.Clear();
        _sender = string.Empty;
    }

    private async Task ReplyAsync(string line)
    {
        _logger.LogTrace("LMTP ← {Line}", line);
        await _writer.WriteLineAsync(line);
    }

    /// <summary>`MAIL FROM:&lt;a@b&gt;` / `RCPT TO:&lt;a@b&gt;` → `a@b`.</summary>
    private static string ExtractPath(string rest)
    {
        var open = rest.IndexOf('<');
        var close = rest.LastIndexOf('>');
        return open >= 0 && close > open ? rest[(open + 1)..close].Trim() : rest.Split(':', 2).ElementAtOrDefault(1)?.Trim() ?? string.Empty;
    }

    /// <summary>What may be logged. Commands carry addresses, never a body — and never a credential.</summary>
    /// <remarks>
    /// LMTP has no AUTH here (the listener is private, per <see cref="LmtpOptions"/>), so there is no
    /// credential-bearing verb to redact. The rule that matters is the one that bit the IMAP trace: whitelist
    /// what is safe rather than guessing where a payload begins. Body lines never reach this method at all —
    /// <see cref="DataAsync"/> reads them directly and logs only a byte count.
    /// </remarks>
    private static string Redact(string line) =>
        line.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase) ? "AUTH ***" : line;
}
