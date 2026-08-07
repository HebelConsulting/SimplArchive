using SimplArchive.Domain.Documents;

namespace SimplArchive.UnitTests;

// The stored form of an @-mention (issue #383). This is a WIRE format — a body written by one client is read by
// the other, by the export archive, and by the interop layer — so the parsing rules are pinned here rather than
// left to whichever caller happens to be looked at first.
public class ChatMentionsTests
{
    private static readonly Guid Alice = Guid.Parse("7f3ac1d2-0000-4000-8000-000000000001");
    private static readonly Guid Bob = Guid.Parse("7f3ac1d2-0000-4000-8000-000000000002");

    [Fact]
    public void A_body_yields_its_mentioned_users_in_the_order_they_appear()
    {
        var body = $"{ChatMentions.Token(Bob)} and {ChatMentions.Token(Alice)}, please look";

        Assert.Equal([Bob, Alice], ChatMentions.Parse(body));
    }

    // The same person named twice is one mention: the subscription and the notification are both per-person, so a
    // second row could only produce a duplicate notify.
    [Fact]
    public void Mentioning_the_same_user_twice_counts_once()
    {
        var body = $"{ChatMentions.Token(Alice)} — {ChatMentions.Token(Alice)}";

        Assert.Equal([Alice], ChatMentions.Parse(body));
    }

    // The pattern runs over every message body that is ever rendered, so prose that merely LOOKS like a token
    // must not become one. A mention carries a real user id or it is not a mention.
    [Theory]
    [InlineData("nothing here")]
    [InlineData("@[not-a-guid]")]
    [InlineData("@[]")]
    [InlineData("see item @[1]")]
    // No braces, and the unbracketed name form the feature deliberately does NOT store — display names have
    // spaces, so this could never have been parsed unambiguously in the first place.
    [InlineData("@Demo Admin")]
    public void Text_that_is_not_a_token_is_not_a_mention(string body) =>
        Assert.Empty(ChatMentions.Parse(body));

    [Fact]
    public void Flatten_renders_names_and_tombstones_the_ones_it_cannot_resolve()
    {
        var body = $"{ChatMentions.Token(Alice)} please ask {ChatMentions.Token(Bob)}";

        var flattened = ChatMentions.Flatten(
            body, id => id == Alice ? "Demo Admin" : null, "Unknown user");

        // Bob does not resolve, so the sentence still reads — the record that somebody was addressed outlives
        // the account, and a raw id in its place would read as a bug.
        Assert.Equal("@Demo Admin please ask @Unknown user", flattened);
    }

    [Fact]
    public void Remap_rewrites_mapped_users_and_leaves_the_rest_alone()
    {
        var target = Guid.Parse("7f3ac1d2-0000-4000-8000-00000000000a");
        var body = $"{ChatMentions.Token(Alice)} + {ChatMentions.Token(Bob)}";

        var remapped = ChatMentions.Remap(body, id => id == Alice ? target : null);

        // An unmapped token stays put rather than being deleted: an import that cannot place a user still shows
        // that somebody was addressed, whereas dropping it would silently rewrite what the author wrote.
        Assert.Equal($"{ChatMentions.Token(target)} + {ChatMentions.Token(Bob)}", remapped);
    }
}
