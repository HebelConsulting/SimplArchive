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
    private readonly PasswordHasher<User> _passwordHasher = new();

    private Stream _stream = Stream.Null;
    private StreamReader _reader = StreamReader.Null;
    private Guid _userId;
    private Guid _tenantId;
    private bool _authenticated;
    private ImapSelectedMailbox? _selected;

    public ImapSession(IServiceScopeFactory scopeFactory, ILogger<ImapSession> logger, X509Certificate2? tlsCertificate)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _tlsCertificate = tlsCertificate;
    }

    public async Task RunAsync(TcpClient client, CancellationToken stopping)
    {
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

            await WriteLineAsync("* OK SimplArchive IMAP4rev1 ready");
            await CommandLoopAsync(stopping);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // A dropped connection is a client's normal way to leave.
        }
        catch (Exception e)
        {
            _logger.LogError(e, "IMAP session failed");
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

            try
            {
                if (!await DispatchAsync(tag, command.ToUpperInvariant(), arguments))
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
                await WriteLineAsync("* CAPABILITY IMAP4rev1 AUTH=PLAIN");
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
                await RunScopedAsync(scope => ImapMailboxes.SelectAsync(this, scope, tag, arguments));
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
            case "CREATE":
            case "DELETE":
            case "RENAME":
                // The mailbox tree IS the archive tree — read-only by design (#562): folders are managed in the
                // workbench, where the ACL and naming rules live.
                await WriteLineAsync($"{tag} NO the folder structure is managed in SimplArchive, not over IMAP");
                return true;
            case "APPEND":
            case "STORE":
            case "EXPUNGE":
            case "MOVE":
            case "COPY":
            case "SEARCH":
            case "UID":
                await WriteLineAsync($"{tag} NO not supported in this slice");
                return true;
            default:
                await WriteLineAsync($"{tag} BAD unknown command");
                return true;
        }
    }

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
            initial = await _reader.ReadLineAsync();
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

        _userId = user.Id;
        _tenantId = user.TenantId;
        _authenticated = true;
        ShowAllDocuments = user.ImapShowAllDocuments;
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
        var bytes = Encoding.Latin1.GetBytes(line + "\r\n");
        await _stream.WriteAsync(bytes);
        await _stream.FlushAsync();
    }

    internal async Task WriteRawAsync(byte[] bytes)
    {
        await _stream.WriteAsync(bytes);
        await _stream.FlushAsync();
    }

    // Reads one command line, absorbing {n}-byte literals (RFC 3501 §4.3): on a trailing {n}, answer the
    // continuation, read exactly n raw characters, and keep reading the rest of the same command.
    private async Task<string?> ReadCommandLineAsync()
    {
        var buffer = new StringBuilder();
        while (true)
        {
            var line = await _reader.ReadLineAsync();
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
                var got = await _reader.ReadAsync(chars.AsMemory(read, n - read));
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
