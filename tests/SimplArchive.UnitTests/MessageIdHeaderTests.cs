using SimplArchive.Presentation;

namespace SimplArchive.UnitTests;

// The shared Message-ID extractor (#704) — ONE implementation both clients call before an upload, so these
// cases are the whole of what "the clients agree" means. The normalized form must equal what the server's
// MimeKit path stores in Entry ID (`<inner>`, exactly one bracket pair); the E2E round trip pins that
// agreement against the real finalizer.
public class MessageIdHeaderTests
{
    [Theory]
    [InlineData("Message-ID: <abc@example.com>\r\nSubject: x\r\n\r\nBody", "<abc@example.com>")]
    // Brackets absent in the source — normalized to exactly one pair, as stored.
    [InlineData("Message-ID: abc@example.com\r\n\r\n", "<abc@example.com>")]
    // The header name is case-insensitive on the wire.
    [InlineData("MESSAGE-ID: <abc@example.com>\r\n\r\n", "<abc@example.com>")]
    [InlineData("message-id:<abc@example.com>\r\n\r\n", "<abc@example.com>")]
    // Bare LF messages exist (files rewritten by tools); the scan must not depend on CRLF.
    [InlineData("Subject: x\nMessage-ID: <abc@example.com>\n\nBody", "<abc@example.com>")]
    public void A_message_id_is_found_and_normalized(string header, string expected) =>
        Assert.Equal(expected, MessageIdHeader.Extract(header));

    [Fact]
    public void A_folded_message_id_is_unfolded()
    {
        // Real senders fold the id onto its own continuation line (RFC 5322 §2.2.3) — a scan that stops at
        // the line break returns an empty id for exactly the messages long ids come from.
        var header = "Subject: x\r\nMessage-ID:\r\n <very-long-id-1234567890@mail.example.com>\r\n\r\nBody";
        Assert.Equal("<very-long-id-1234567890@mail.example.com>", MessageIdHeader.Extract(header));
    }

    [Fact]
    public void A_message_id_in_the_BODY_is_never_matched()
    {
        // The blank line ends the headers. A message QUOTING another message's headers in its body must not
        // inherit that message's identity — that would make every reply-with-quote a "duplicate".
        var text = "Subject: x\r\n\r\nQuoted:\r\nMessage-ID: <other@example.com>\r\n";
        Assert.Null(MessageIdHeader.Extract(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Subject: no id here\r\n\r\n")]
    // An empty value normalizes to nothing, not to "<>".
    [InlineData("Message-ID: <>\r\n\r\n")]
    [InlineData("Message-ID:\r\n\r\n")]
    public void No_id_means_null_and_the_probe_stays_hash_only(string? header) =>
        Assert.Null(MessageIdHeader.Extract(header));
}
