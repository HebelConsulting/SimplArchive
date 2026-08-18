using SimplArchive.Api.Imap;

namespace SimplArchive.UnitTests;

// The IMAP session logs each command with its ARGUMENTS at Debug, because a client-interop question turns on
// exactly those — which mailbox, which sequence set, which fetch items. For two commands that argument string
// IS the password, so the logging convention's "never log secrets" rule has a single enforcement point here.
//
// Worth a test rather than a careful reading: the failure is silent, survives review, and is discovered by
// finding credentials in a log aggregator that many people can read.
public class ImapLoggingRedactionTests
{
    [Theory]
    [InlineData("LOGIN")]
    [InlineData("AUTHENTICATE")]
    public void A_credential_bearing_command_never_returns_its_arguments(string command)
    {
        const string secret = "hunter2-the-actual-password";

        // The real shapes: LOGIN takes "user password", AUTHENTICATE PLAIN a base64 blob carrying both.
        Assert.DoesNotContain(secret, ImapSession.Redact(command, $"someone@example.com {secret}"), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, ImapSession.Redact(command, $"PLAIN {Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"\0someone@example.com\0{secret}"))}"), StringComparison.Ordinal);

        // Nothing of the argument survives — not a prefix, not a length hint. The redactor deliberately does not
        // parse the format, because one that has to be right about the format leaks the first time it varies.
        Assert.Equal("***", ImapSession.Redact(command, $"someone@example.com {secret}"));
    }

    [Theory]
    [InlineData("SELECT", "\"Demo Repository/Invoices\"")]
    [InlineData("UID FETCH", "1:* (UID FLAGS BODY.PEEK[])")]
    [InlineData("LIST", "\"\" \"*\"")]
    [InlineData("STATUS", "INBOX (MESSAGES UNSEEN)")]
    public void Every_other_command_keeps_its_arguments(string command, string arguments)
    {
        // The counterpart assertion, and the one that keeps the redactor honest: blanket-redacting everything
        // would pass the test above and destroy the reason the Debug line exists. A mailbox name and a fetch
        // item set are not secrets, and without them the log cannot answer "what did that client ask for?".
        Assert.Equal(arguments, ImapSession.Redact(command, arguments));
    }

    // APPEND's argument IS the message, so the Debug line was writing document content — personal data — into
    // a log that is on by default in Development.
    //
    // BOTH encodings are here on purpose, and the second one is the point: a length cap and a cut at IMAP's
    // literal marker each passed a test written from the same assumption as the code, and each still leaked
    // against a real client. The quoted form is what MailKit actually sends.
    [Theory]
    [InlineData("Notes {2048}\r\nFrom: someone@example.com\r\nSubject: Salary review\r\n\r\nbody text here")]
    [InlineData("Notes \"From: someone@example.com Date: Tue, 18 Aug 2026 08:35:30 +0200 Subject: Salary review\"")]
    [InlineData("\"Demo Repository/Invoices\" (\\Seen) \"From: a@b.c Subject: Salary review\"")]
    public void A_payload_bearing_command_keeps_only_its_mailbox(string arguments)
    {
        var logged = ImapSession.Redact("APPEND", arguments);

        Assert.DoesNotContain("Salary review", logged, StringComparison.Ordinal);
        Assert.DoesNotContain("someone@example.com", logged, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", logged, StringComparison.Ordinal); // one line, not a smuggled blob

        // What survives is the addressing — which is the whole reason to log the command at all.
        Assert.Contains("{…}", logged, StringComparison.Ordinal);
        Assert.StartsWith(arguments.TrimStart()[..5], logged, StringComparison.Ordinal);
    }

    [Fact]
    public void The_rule_is_case_insensitive_at_the_call_site()
    {
        // Redact is given the already-upper-cased verb the dispatcher switches on, so lower-case input is not a
        // case it must handle — but if that ever changes, a lower-case "login" must not slip through silently.
        // Asserting today's contract explicitly is what makes such a change fail here rather than in production.
        Assert.Equal("***", ImapSession.Redact("LOGIN", "user secret"));
        Assert.Equal("user secret", ImapSession.Redact("login", "user secret"));
    }
}
