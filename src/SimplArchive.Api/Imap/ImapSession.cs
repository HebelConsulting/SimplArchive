using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Imap;

/// <summary>
/// One IMAP connection (ADR "IMAP endpoint (read-only, first slice)"): greeting, command loop, authentication,
/// and dispatch. Read-only in this slice — the write commands answer NO, and the structure commands
/// (CREATE/DELETE/RENAME mailbox) answer NO by design: the tree is the archive's, not the mail client's.
/// </summary>
/// <remarks>
/// Every command that touches data runs in its own DI scope with the tenant/user accessors set from the
/// authenticated login — the IMAP twin of what CurrentPrincipalMiddleware does per HTTP request, so the
/// tenant query filter scopes every read exactly as it would for the workbench.
/// </remarks>
public sealed class ImapSession
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ImapSession> _logger;
    private readonly X509Certificate2? _tlsCertificate;
    private readonly ImapOptions _options;
    private readonly ImapConnectionRegistry _registry;
    private readonly PasswordHasher<User> _passwordHasher = new();

    private Stream _stream = Stream.Null;
    private StreamReader _reader = StreamReader.Null;
    private Guid _userId;
    private Guid _tenantId;
    private bool _authenticated;
    private ImapSelectedMailbox? _selected;

    // Counters for the ONE summary line a finished session emits (below). A protocol session is the IMAP
    // analogue of an HTTP request, so it is summarised the way UseSerilogRequestLogging summarises those —
    // one line with an outcome, rather than a running commentary at Information.
    private readonly long _startedAt = Stopwatch.GetTimestamp();
    private string _email = "anonymous";
    private int _commands;
    private int _refused;

    internal ImapSession(IServiceScopeFactory scopeFactory, ILogger<ImapSession> logger, X509Certificate2? tlsCertificate, ImapOptions options, ImapConnectionRegistry registry)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _tlsCertificate = tlsCertificate;
        _options = options;
        _registry = registry;
    }

    public async Task RunAsync(TcpClient client, CancellationToken stopping)
    {
        var counted = _registry.TryAddConnection(_options.MaxConnections);
        try
        {
            using var _ = client;
            Stream raw = client.GetStream();
            if (_tlsCertificate is not null)
            {
                var ssl = new SslStream(raw);
                await ssl.AuthenticateAsServerAsync(_tlsCertificate);
                raw = ssl;
            }

            _stream = raw;
            // IMAP is line-oriented ASCII with explicit {n} literals; Latin-1 keeps raw bytes round-trippable.
            _reader = new StreamReader(raw, Encoding.Latin1, false, 4096, leaveOpen: true);

            if (!counted)
            {
                // The total cap (ADR 0618) refuses at the greeting — a polite BYE instead of a silent close,
                // after the TLS handshake so a TLS client can actually read it.
                _logger.LogWarning("IMAP connection refused: total connection cap ({MaxConnections}) reached", _options.MaxConnections);
                await WriteLineAsync("* BYE too many connections");
                return;
            }

            _logger.LogDebug(
                "IMAP connection from {RemoteEndPoint} ({Transport})",
                client.Client.RemoteEndPoint, _tlsCertificate is null ? "plaintext" : "TLS");

            await WriteLineAsync("* OK SimplArchive IMAP4rev1 ready");
            await CommandLoopAsync(stopping);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // A dropped connection is a client's normal way to leave; the idle/pre-auth autologout (ADR 0618)
            // also lands here after its BYE.
        }
        catch (Exception e)
        {
            _logger.LogError(e, "IMAP session failed");
        }
        finally
        {
            if (_authenticated)
            {
                _registry.RemoveUser(_userId);
            }

            if (counted)
            {
                _registry.RemoveConnection();
            }

            // The session summary. {Refused} earns its place in the template rather than being left to the
            // per-command Debug line: a client that cannot work because we answer NO to something it needs
            // looks IDENTICAL to a healthy one at Information, which is how an unimplemented mandatory command
            // stayed invisible while a device silently showed empty folders.
            _logger.LogInformation(
                "IMAP session {Email} ended: {Commands} commands, {Refused} refused, in {ElapsedMs} ms",
                _email, _commands, _refused,
                (int)Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds);
        }
    }

    private async Task CommandLoopAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            var line = await ReadCommandLineAsync();
            if (line is null)
            {
                return; // client closed the connection
            }

            var (tag, command, arguments) = SplitCommand(line);
            if (tag.Length == 0 || command.Length == 0)
            {
                await WriteLineAsync("* BAD malformed command");
                continue;
            }

            var verb = command.ToUpperInvariant();
            _commands++;

            // Per command, at Debug: the verb and its ARGUMENTS, which for IMAP are the mailbox, the sequence
            // set or the fetch items — i.e. exactly what a client-interop question turns on. Redacted, because
            // LOGIN and AUTHENTICATE carry the password in that same position.
            _logger.LogDebug("IMAP {Email} > {Command} {Arguments}", _email, verb, Redact(verb, arguments));

            try
            {
                if (!await DispatchAsync(tag, verb, arguments))
                {
                    return; // LOGOUT
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "IMAP command {Command} failed", command);
                await WriteLineAsync($"{tag} NO server error");
            }
        }
    }

    private async Task<bool> DispatchAsync(string tag, string command, string arguments)
    {
        switch (command)
        {
            case "CAPABILITY":
                await WriteLineAsync("* CAPABILITY IMAP4rev1 AUTH=PLAIN MOVE UIDPLUS SPECIAL-USE");
                await OkAsync(tag, "CAPABILITY");
                return true;
            case "NOOP":
                await OkAsync(tag, "NOOP");
                return true;
            case "LOGOUT":
                await WriteLineAsync("* BYE SimplArchive IMAP4rev1 logging out");
                await OkAsync(tag, "LOGOUT");
                return false;
            case "LOGIN":
                await LoginAsync(tag, arguments);
                return true;
            case "AUTHENTICATE":
                await AuthenticatePlainAsync(tag, arguments);
                return true;
        }

        if (!_authenticated)
        {
            await WriteLineAsync($"{tag} NO not authenticated");
            return true;
        }

        switch (command)
        {
            case "NAMESPACE":
                // One personal namespace, no shared/other-user namespaces — the shared repositories are plain
                // top-level mailboxes in the same space, which is how the WebDAV mount presents them too.
                await WriteLineAsync("""* NAMESPACE (("" "/")) NIL NIL""");
                await OkAsync(tag, "NAMESPACE");
                return true;
            case "LIST":
            case "LSUB":
                await RunScopedAsync(scope => ImapMailboxes.ListAsync(this, scope, tag, command, arguments));
                return true;
            case "SUBSCRIBE":
            case "UNSUBSCRIBE":
                // Accepted as no-ops for client compatibility — subscription state is meaningless here, and a
                // NO would make some clients refuse to show the mailbox at all.
                await OkAsync(tag, command);
                return true;
            case "STATUS":
                await RunScopedAsync(scope => ImapMailboxes.StatusAsync(this, scope, tag, arguments));
                return true;
            case "SELECT":
            case "EXAMINE":
                await RunScopedAsync(scope => ImapMailboxes.SelectAsync(this, scope, tag, arguments, readOnly: command == "EXAMINE"));
                return true;
            case "CLOSE":
                _selected = null;
                await OkAsync(tag, "CLOSE");
                return true;
            case "CHECK":
                await OkAsync(tag, "CHECK");
                return true;
            case "FETCH":
            case "UID" when arguments.StartsWith("FETCH ", StringComparison.OrdinalIgnoreCase):
                if (_selected is null)
                {
                    await WriteLineAsync($"{tag} NO no mailbox selected");
                    return true;
                }

                var uidMode = command == "UID";
                var fetchArguments = uidMode ? arguments["FETCH ".Length..] : arguments;
                await RunScopedAsync(scope => ImapFetch.FetchAsync(this, scope, tag, _selected, fetchArguments, uidMode));
                return true;
            case "STORE":
            case "UID" when arguments.StartsWith("STORE ", StringComparison.OrdinalIgnoreCase):
                if (_selected is null)
                {
                    await WriteLineAsync($"{tag} NO no mailbox selected");
                    return true;
                }

                if (_selected.ReadOnly)
                {
                    await WriteLineAsync($"{tag} NO the mailbox is open read-only");
                    return true;
                }

                var storeUidMode = command == "UID";
                var storeArguments = storeUidMode ? arguments["STORE ".Length..] : arguments;
                await RunScopedAsync(scope => ImapStore.StoreAsync(this, scope, tag, _selected, storeArguments, storeUidMode));
                return true;
            case "APPEND":
                await RunScopedAsync(scope => ImapWrites.AppendAsync(this, scope, tag, arguments));
                return true;
            case "EXPUNGE":
                if (_selected is null || _selected.ReadOnly)
                {
                    await WriteLineAsync($"{tag} NO no writable mailbox selected");
                    return true;
                }

                await RunScopedAsync(scope => ImapWrites.ExpungeAsync(this, scope, tag, _selected));
                return true;
            case "MOVE":
            case "COPY":
            case "UID" when arguments.StartsWith("MOVE ", StringComparison.OrdinalIgnoreCase)
                || arguments.StartsWith("COPY ", StringComparison.OrdinalIgnoreCase):
                if (_selected is null)
                {
                    await WriteLineAsync($"{tag} NO no mailbox selected");
                    return true;
                }

                var mcUid = command == "UID";
                var mcMove = (mcUid ? arguments : command).StartsWith("MOVE", StringComparison.OrdinalIgnoreCase);
                var mcArguments = mcUid ? arguments[(mcMove ? "MOVE " : "COPY ").Length..] : arguments;
                await RunScopedAsync(scope => ImapWrites.MoveOrCopyAsync(this, scope, tag, _selected, mcArguments, mcUid, mcMove));
                return true;
            case "CREATE":
                // The one opening in the read-only tree (#564): a section inside the notebook. Everything else
                // is refused by CreateAsync itself, with the same sentence DELETE and RENAME give.
                await RunScopedAsync(scope => ImapWrites.CreateAsync(this, scope, tag, arguments));
                return true;
            case "DELETE":
            case "RENAME":
                // The mailbox tree IS the archive tree — read-only by design (#562): folders are managed in the
                // workbench, where the ACL and naming rules live.
                await WriteLineAsync($"{tag} NO the folder structure is managed in SimplArchive, not over IMAP");
                return true;
            // SEARCH is MANDATORY in the IMAP4rev1 we advertise, and refusing it is what made a mail client
            // that enumerates with UID SEARCH show every folder as empty (ADR 0626).
            case "SEARCH":
            case "UID" when arguments.StartsWith("SEARCH ", StringComparison.OrdinalIgnoreCase):
                if (_selected is null)
                {
                    await WriteLineAsync($"{tag} NO no mailbox selected");
                    return true;
                }

                var searchUidMode = command == "UID";
                var searchArguments = searchUidMode ? arguments["SEARCH ".Length..] : arguments;
                await RunScopedAsync(scope => ImapSearch.SearchAsync(this, scope, tag, _selected, searchArguments, searchUidMode));
                return true;
            case "UID":
                await RefuseAsync(tag, command, "not implemented in this slice");
                return true;
            default:
                await RefuseAsync(tag, command, "unknown command", bad: true);
                return true;
        }
    }

    /// <summary>
    /// Answers a command we do not serve, and says so in the log at <b>Warning</b>.
    /// </summary>
    /// <summary>
    /// Something the client asked for that we answered with SOMETHING ELSE rather than with an error.
    /// </summary>
    /// <remarks>
    /// The sibling of <see cref="RefuseAsync"/>, for the case that is worse than a refusal: a refusal reaches
    /// the client, a substitution does not. The client believes it has what it asked for, and the user sees a
    /// corrupt file rather than an error — which is exactly how BODY[&lt;part&gt;] came to be reported as
    /// "the PDF downloads corrupted" (#766) after silently answering with the whole message for months.
    /// Warning, and it names the switch, for the reasons ADR 0626 gives.
    /// </remarks>
    internal void WarnSubstituted(string asked, string served)
    {
        _logger.LogWarning(
            "IMAP {Email}: asked for {Asked} and served {Served} instead — the client cannot tell, and will "
            + "present the result as though it were what it requested; set Serilog:MinimumLevel:Override:{LogSource} "
            + "to Trace to see the exchange",
            _email, asked, served, TraceSource);
    }

    /// <remarks>
    /// Warning rather than Debug because this is the definition of it: an administrator very likely needs to
    /// act. A refusal here is not the client's mistake — we advertise <c>IMAP4rev1</c>, whose mandatory command
    /// set a client is entitled to rely on, so a client asking for one of those and being told NO will simply
    /// not work, and will not say why. That is not hypothetical: <c>SEARCH</c> is mandatory and unimplemented,
    /// and a mail client that enumerates with <c>UID SEARCH</c> showed every folder as EMPTY while another
    /// client on the same account worked perfectly, because it enumerated with FETCH instead. Nothing in the
    /// log distinguished the two. This line does.
    /// </remarks>
    private async Task RefuseAsync(string tag, string command, string reason, bool bad = false)
    {
        _refused++;

        // Names the SWITCH, not just the problem (ADR 0626). An administrator reading "refused SEARCH" still
        // has to guess which knob shows more; naming the source override removes the guess, and it is the
        // difference between a note and an instruction.
        _logger.LogWarning(
            "IMAP {Email}: refused {Command} — {Reason}. A client relying on it will misbehave SILENTLY; "
            + "set Serilog:MinimumLevel:Override:{LogSource} to Trace to see the exchange",
            _email, command, reason, TraceSource);
        await WriteLineAsync($"{tag} {(bad ? "BAD" : "NO")} {reason}");
    }

    /// <summary>The arguments as they may be LOGGED — never the credential ones, never a whole payload.</summary>
    /// <remarks>
    /// <para>
    /// LOGIN and AUTHENTICATE carry the password in the argument position, so the whole argument string is
    /// dropped for them rather than parsed and partially kept: a redactor that has to be right about the
    /// format is one that leaks the first time the format varies.
    /// </para>
    /// <para>
    /// Everything else is TRUNCATED rather than passed through, because APPEND's argument is the entire
    /// message — the untruncated line wrote document content, and therefore personal data, into a log that is
    /// on by default in Development. Found by reading the real output, not by reasoning about it. The cap is
    /// generous enough for what this line exists to answer: a mailbox name, a sequence set and a fetch-item
    /// list all fit well inside it.
    /// </para>
    /// </remarks>
    internal static string Redact(string command, string arguments)
    {
        if (command is "LOGIN" or "AUTHENTICATE")
        {
            return "***";
        }

        // APPEND carries a whole message, so it is handled like a credential: keep the one part that is
        // addressing (the mailbox) and drop everything after it, whatever the encoding.
        //
        // Two weaker rules were tried and both leaked. A 120-character cap leaks, because a message's first
        // bytes ARE its headers — `From:` and `Subject:` fit inside any cap worth having. Cutting at IMAP's
        // literal marker `{n}` leaks too, because a client may send the message as a QUOTED STRING instead,
        // and a real one does. Both were caught by reading actual output; neither was caught by a unit test
        // written from the same assumption as the code. So the rule keeps a whitelist rather than trying to
        // find where the payload starts.
        if (command is "APPEND")
        {
            var mailbox = arguments.AsSpan().TrimStart();
            var end = mailbox.IndexOf(' ');
            return $"{(end < 0 ? mailbox : mailbox[..end]).ToString()} {{…}}";
        }

        var oneLine = arguments.ReplaceLineEndings(" ").Trim();
        return oneLine.Length <= MaxLoggedArgumentLength
            ? oneLine
            : $"{oneLine[..MaxLoggedArgumentLength]}…";
    }

    private const int MaxLoggedArgumentLength = 120;

    /// <summary>
    /// A SEARCH we could not evaluate because one of its keys is unimplemented. Loud and switch-naming like
    /// any other fall-through — but scoped to the key, so the client learns this search failed rather than
    /// concluding the mailbox is empty.
    /// </summary>
    internal Task RefuseSearchAsync(string tag, string criterion) =>
        RefuseAsync(tag, $"SEARCH {criterion}", $"unsupported search key {criterion}");

    /// <summary>The Serilog source an administrator raises to Trace to see the whole exchange (ADR 0626).</summary>
    /// <remarks>
    /// Spelled out rather than taken from <c>typeof(ImapSession).FullName</c> so that renaming or moving this
    /// class cannot silently change the instruction we print — a wrong switch name is worse than none, because
    /// it is followed.
    /// </remarks>
    private const string TraceSource = "SimplArchive.Api.Imap";

    // ---- Authentication ------------------------------------------------------------------------------

    private async Task LoginAsync(string tag, string arguments)
    {
        var parts = ImapProtocol.Tokenize(arguments);
        if (parts.Count != 2)
        {
            await WriteLineAsync($"{tag} BAD LOGIN expects a username and a password");
            return;
        }

        await AuthenticateAsync(tag, parts[0], parts[1]);
    }

    private async Task AuthenticatePlainAsync(string tag, string arguments)
    {
        if (!arguments.StartsWith("PLAIN", StringComparison.OrdinalIgnoreCase))
        {
            await WriteLineAsync($"{tag} NO only AUTH=PLAIN is supported");
            return;
        }

        // SASL PLAIN: base64("authzid\0authcid\0password"), either as an initial response or after a "+".
        var initial = arguments.Length > "PLAIN".Length ? arguments["PLAIN".Length..].Trim() : null;
        if (string.IsNullOrEmpty(initial))
        {
            await WriteLineAsync("+ ");
            initial = await ReadTimedLineAsync();
            if (initial is null)
            {
                return;
            }
        }

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(initial.Trim()));
        }
        catch (FormatException)
        {
            await WriteLineAsync($"{tag} BAD malformed SASL response");
            return;
        }

        var pieces = decoded.Split('\0');
        if (pieces.Length != 3)
        {
            await WriteLineAsync($"{tag} BAD malformed SASL response");
            return;
        }

        await AuthenticateAsync(tag, pieces[1], pieces[2]);
    }

    private async Task AuthenticateAsync(string tag, string email, string password)
    {
        // Re-authenticating an authenticated session would double-count it in the per-user registry (its
        // release happens once, at session end) — and RFC 3501 forbids it anyway.
        if (_authenticated)
        {
            await WriteLineAsync($"{tag} NO already authenticated");
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();

        // The login runs before any tenant is known — the same cross-tenant email resolution as the login page
        // (the tenant filter's TenantId == null predicate would otherwise match nothing).
        var normalized = email.Trim().ToUpperInvariant();
        var user = await db.Users.IgnoreQueryFilters(["TenantFilter"])
            .FirstOrDefaultAsync(u => u.NormalizedEmail == normalized && u.IsActive);

        var tenantActive = user is not null
            && await db.Tenants.AnyAsync(t => t.Id == user.TenantId && t.Status == TenantStatus.Active);

        if (user?.ImapPasswordHash is null
            || !tenantActive
            || _passwordHasher.VerifyHashedPassword(user, user.ImapPasswordHash, password) == PasswordVerificationResult.Failed)
        {
            // One failure message for every cause — a prober learns nothing about which accounts exist or
            // have IMAP enabled. The failed attempt is Warning-logged for SIEM aggregation, like a failed login.
            _logger.LogWarning("IMAP authentication failed for {Email}", email);
            await WriteLineAsync($"{tag} NO authentication failed");
            return;
        }

        // The per-user cap counts at successful authentication (ADR 0618) — before it the user is unknown.
        // The refused session stays unauthenticated, so the pre-auth timeout keeps it on the short leash.
        if (!_registry.TryAddUser(user.Id, _options.MaxConnectionsPerUser))
        {
            _logger.LogDebug("IMAP connection refused for {Email}: per-user connection cap ({MaxConnectionsPerUser}) reached", email, _options.MaxConnectionsPerUser);
            await WriteLineAsync($"{tag} NO too many connections for this user");
            return;
        }

        _userId = user.Id;
        _tenantId = user.TenantId;
        _authenticated = true;
        _email = user.Email;
        ShowAllDocuments = user.ImapShowAllDocuments;

        // A sign-in is a security-relevant SUCCESS, which is Information by the logging convention — the
        // counterpart of the Warning a failure already emits, so a SIEM sees both sides. ShowAllDocuments
        // rides along because it decides whether this session can see anything but emails, and "the folders
        // are empty" is the first thing it is asked about.
        _logger.LogInformation(
            "IMAP sign-in for {Email} (tenant {TenantId}, all documents: {ShowAllDocuments})",
            _email, _tenantId, ShowAllDocuments);

        await OkAsync(tag, "authenticated");
    }

    // ---- Scoped access + shared session state --------------------------------------------------------

    /// <summary>The authenticated user's view choice (#562): false = emails only, true = every visible document.</summary>
    public bool ShowAllDocuments { get; private set; }

    public Guid UserId => _userId;

    internal ImapSelectedMailbox? Selected
    {
        get => _selected;
        set => _selected = value;
    }

    internal async Task RunScopedAsync(Func<IServiceScope, Task> action)
    {
        using var scope = _scopeFactory.CreateScope();
        ((CurrentTenantAccessor)scope.ServiceProvider.GetRequiredService<SimplArchive.Application.Abstractions.ICurrentTenantAccessor>()).TenantId = _tenantId;
        ((CurrentUserAccessor)scope.ServiceProvider.GetRequiredService<SimplArchive.Application.Abstractions.ICurrentUserAccessor>()).UserId = _userId;
        await action(scope);
    }

    // ---- Wire helpers --------------------------------------------------------------------------------

    internal Task OkAsync(string tag, string what) => WriteLineAsync($"{tag} OK {what} completed");

    internal async Task WriteLineAsync(string line)
    {
        // The SERVER half of the exchange, at Trace (ADR 0626). The client half is already the per-command
        // Debug line, and Debug is above Trace — so turning Trace on for this source yields BOTH halves and
        // therefore the whole conversation, without either being logged twice.
        //
        // Verbatim is safe HERE and only here: message bodies do not travel this path. A FETCH writes its
        // protocol line through this method and the content itself through WriteRawAsync below, which is what
        // lets the response lines be recorded exactly as sent while the payload never is.
        _logger.LogTrace("IMAP {Email} S: {Line}", _email, line);

        var bytes = Encoding.Latin1.GetBytes(line + "\r\n");
        await _stream.WriteAsync(bytes);
        await _stream.FlushAsync();
    }

    internal async Task WriteRawAsync(byte[] bytes)
    {
        // A payload: its SIZE is the diagnostic fact, its content is somebody's document. Never the content —
        // not even at Trace, which is the one place a "raw payloads" reading of the levels would allow it.
        _logger.LogTrace("IMAP {Email} S: <{Bytes} bytes of content>", _email, bytes.Length);

        await _stream.WriteAsync(bytes);
        await _stream.FlushAsync();
    }

    // Every read waits under a budget (ADR 0618): the short pre-auth leash until login succeeds, RFC 3501's
    // 30-minute autologout floor after. On expiry the session says BYE and leaves through the same IOException
    // path as a dropped connection. The abandoned read's later fault (when the socket closes under it) is
    // observed so it can't surface as an unobserved task exception.
    private async Task<T> WithReadTimeoutAsync<T>(Task<T> read)
    {
        var timeout = TimeSpan.FromSeconds(_authenticated ? _options.IdleTimeoutSeconds : _options.PreAuthTimeoutSeconds);
        if (await Task.WhenAny(read, Task.Delay(timeout)) != read)
        {
            _ = read.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            await WriteLineAsync(_authenticated ? "* BYE autologout; idle for too long" : "* BYE login timed out");
            throw new IOException("IMAP read timed out");
        }

        return await read;
    }

    private Task<string?> ReadTimedLineAsync() => WithReadTimeoutAsync(_reader.ReadLineAsync());

    // Reads one command line, absorbing {n}-byte literals (RFC 3501 §4.3): on a trailing {n}, answer the
    // continuation, read exactly n raw characters, and keep reading the rest of the same command.
    private async Task<string?> ReadCommandLineAsync()
    {
        var buffer = new StringBuilder();
        while (true)
        {
            var line = await ReadTimedLineAsync();
            if (line is null)
            {
                return buffer.Length == 0 ? null : buffer.ToString();
            }

            var literal = ImapProtocol.TrailingLiteralLength(line);
            if (literal is not { } n)
            {
                return buffer.Append(line).ToString();
            }

            buffer.Append(line, 0, line.LastIndexOf('{'));
            await WriteLineAsync("+ OK");
            var chars = new char[n];
            var read = 0;
            while (read < n)
            {
                var got = await WithReadTimeoutAsync(_reader.ReadAsync(chars.AsMemory(read, n - read)).AsTask());
                if (got == 0)
                {
                    return null;
                }

                read += got;
            }

            // Re-quote the literal as a quoted string for the tokenizer; IMAP literals may contain anything,
            // but mailbox names/credentials — the only literals this slice accepts — survive this round trip.
            buffer.Append('"').Append(new string(chars).Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
        }
    }

    private static (string Tag, string Command, string Arguments) SplitCommand(string line)
    {
        var firstSpace = line.IndexOf(' ');
        if (firstSpace < 0)
        {
            return (line, string.Empty, string.Empty);
        }

        var rest = line[(firstSpace + 1)..];
        var secondSpace = rest.IndexOf(' ');
        return secondSpace < 0
            ? (line[..firstSpace], rest, string.Empty)
            : (line[..firstSpace], rest[..secondSpace], rest[(secondSpace + 1)..]);
    }
}
