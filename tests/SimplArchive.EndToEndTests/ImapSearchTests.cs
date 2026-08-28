using System.Net.Http.Json;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using SimplArchive.Api.Imap;

namespace SimplArchive.EndToEndTests;

// SEARCH / UID SEARCH (RFC 3501 §6.4.4), driven with a REAL mail-client library, because that is the only way
// this defect was ever going to be found.
//
// It was refused — "NO not supported in this slice" — while the greeting advertised IMAP4rev1, in which SEARCH
// is MANDATORY. A client that enumerates a mailbox with UID SEARCH therefore concluded there were no messages
// and displayed EVERY FOLDER AS EMPTY, while another client on the same account worked perfectly because it
// enumerates with FETCH. Both clients were "working"; only one asked a question we refused to answer.
//
// So these tests use MailKit's own query API rather than hand-writing the wire commands: the point is not that
// our parser accepts strings we chose, it is that a client's own idea of a search gets a usable answer.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class ImapSearchTests
{
    private readonly E2EApiFactory _factory;

    public ImapSearchTests(E2EApiFactory factory) => _factory = factory;

    private async Task<(ImapClient Client, IMailFolder Notes, HttpClient Api)> ConnectWithTwoNotesAsync()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"imap-search-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "search-1234", "Search User");
        var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "search-1234"));
        var imapPassword = (await TestJson.Post(api, "/api/me/imap-access", new { })).GetProperty("password").GetString()!;
        await TestJson.Post(api, "/api/me/personal-repository", new { });

        var port = ((ImapServer)_factory.Services.GetService(typeof(ImapServer))!).BoundPort!.Value;
        var client = new ImapClient();
        await client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.None);
        await client.AuthenticateAsync(email, imapPassword);

        // CREATE it first, exactly as a notes client does on an account it has not used before: the notebook
        // is not provisioned, and `CREATE "Notes"` is the call that brings it into being (#596).
        await client.GetFolder(client.PersonalNamespaces[0]).CreateAsync("Notes", true);
        var notes = await client.GetFolderAsync("Notes");

        MimeKit.MimeMessage Note(string subject, string body)
        {
            var m = new MimeKit.MimeMessage();
            m.From.Add(new MimeKit.MailboxAddress("Me", email));
            m.Subject = subject;
            m.Headers.Add("X-Universally-Unique-Identifier", Guid.NewGuid().ToString());
            m.Body = new MimeKit.TextPart("plain") { Text = body };
            return m;
        }

        await notes.AppendAsync(Note("Shopping list", "eggs and milk"));
        await notes.AppendAsync(Note("Rent reminder", "transfer on the first"));

        // Fill the mailbox BEFORE opening it. A selected mailbox is a snapshot taken at SELECT — the same
        // contract FETCH and STORE already work to — so a message appended after the open is not in this
        // session's view of it, and a client learns about it from an untagged EXISTS or a re-select. Opening
        // last is what a client does anyway; doing it first made the first version of these tests measure the
        // snapshot rather than the search.
        await notes.OpenAsync(FolderAccess.ReadWrite);
        return (client, notes, api);
    }

    [Fact]
    public async Task A_client_enumerating_with_search_gets_its_messages()
    {
        var (client, notes, api) = await ConnectWithTwoNotesAsync();
        using var _1 = api;
        using var _2 = client;

        // THE regression test. MailKit's Search issues UID SEARCH — the command that used to answer NO, which
        // is why a whole mailbox rendered empty. Two notes were filed, so two must come back.
        var all = await notes.SearchAsync(SearchQuery.All);
        Assert.Equal(2, all.Count);

        await client.DisconnectAsync(true);
    }

    [Fact]
    public async Task Searching_narrows_by_flag_by_header_and_by_size()
    {
        var (client, notes, api) = await ConnectWithTwoNotesAsync();
        using var _1 = api;
        using var _2 = client;

        // A header criterion reads the message, and must match the ONE note that carries the subject — not
        // both, and not none. This is the criterion class that touches object storage.
        var shopping = await notes.SearchAsync(SearchQuery.SubjectContains("Shopping"));
        Assert.Single(shopping);

        var body = await notes.SearchAsync(SearchQuery.BodyContains("eggs"));
        Assert.Single(body);

        // Flags: everything starts unseen, and marking one moves it between the two answers. Asserting both
        // directions is the point — a criterion that always returned everything would pass a NotSeen check
        // taken alone.
        var unseenBefore = await notes.SearchAsync(SearchQuery.NotSeen);
        Assert.Equal(2, unseenBefore.Count);

        await notes.AddFlagsAsync(shopping, MessageFlags.Seen, silent: true);
        Assert.Single(await notes.SearchAsync(SearchQuery.NotSeen));
        Assert.Single(await notes.SearchAsync(SearchQuery.Seen));

        // A size bound both ways, so "larger" cannot be satisfied by an implementation that ignores the number.
        Assert.Equal(2, (await notes.SearchAsync(SearchQuery.LargerThan(10))).Count);
        Assert.Empty(await notes.SearchAsync(SearchQuery.LargerThan(10_000_000)));

        await client.DisconnectAsync(true);
    }

    [Fact]
    public async Task Combining_criteria_and_finding_nothing_are_both_answers_not_errors()
    {
        var (client, notes, api) = await ConnectWithTwoNotesAsync();
        using var _1 = api;
        using var _2 = client;

        // AND, OR and NOT — a client composes these freely, and each must narrow rather than be ignored.
        Assert.Single(await notes.SearchAsync(SearchQuery.NotSeen.And(SearchQuery.SubjectContains("Rent"))));
        Assert.Equal(2, (await notes.SearchAsync(
            SearchQuery.SubjectContains("Rent").Or(SearchQuery.SubjectContains("Shopping")))).Count);
        Assert.Single(await notes.SearchAsync(SearchQuery.Not(SearchQuery.SubjectContains("Rent"))));

        // A search matching NOTHING is an empty result, never a failure — the distinction the original defect
        // erased. MailKit would throw on a NO, so reaching the assertion is itself half the test.
        Assert.Empty(await notes.SearchAsync(SearchQuery.SubjectContains($"absent-{Guid.NewGuid():N}")));

        // Dates are day-granular and inclusive on SINCE; everything here was filed today.
        Assert.Equal(2, (await notes.SearchAsync(
            SearchQuery.DeliveredAfter(DateTime.UtcNow.Date.AddDays(-1)))).Count);
        Assert.Empty(await notes.SearchAsync(SearchQuery.DeliveredBefore(DateTime.UtcNow.Date.AddDays(-1))));

        await client.DisconnectAsync(true);
    }
}
