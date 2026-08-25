using SimplArchive.Api.Documents;

namespace SimplArchive.UnitTests;

// What may be typed into a box asking for a mail domain (#667). A SHAPE check only — whether the domain exists
// and whether the tenant owns it is what the DNS challenge answers, and refusing a syntactically fine name
// because a resolver was slow would be a worse error than the one this prevents.
public class MailDomainNameTests
{
    [Theory]
    [InlineData("example.com")]
    [InlineData("example.co.uk")]
    [InlineData("EXAMPLE.COM")]          // case is not the key; NormalizedDomain is
    [InlineData("a-b.example.com")]
    [InlineData("xn--bcher-kva.example")] // punycode is ordinary letters and digits by the time it gets here
    public void A_domain_is_accepted(string domain) => Assert.True(MailDomainName.IsWellFormed(domain));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("admin@example.com")]     // the address, not the domain — the commonest mistake by far
    [InlineData("https://example.com")]   // a pasted web address
    [InlineData("example.com/inbox")]
    [InlineData("example.com:25")]
    [InlineData("example com")]
    [InlineData("localhost")]             // one label is a host on a search domain, never a mail domain
    [InlineData("-example.com")]          // a label may not start or end with a hyphen
    [InlineData("example-.com")]
    [InlineData("exa_mple.com")]          // underscore is not a hostname character
    [InlineData("example..com")]          // an empty label
    public void A_value_that_is_not_a_domain_is_refused(string? value) =>
        Assert.False(MailDomainName.IsWellFormed(value));

    [Fact]
    public void A_name_longer_than_dns_allows_is_refused()
    {
        // 253 is the maximum for a fully-qualified name, so a longer one is not a domain at all — and the
        // column is sized to it, so accepting one would fail at the database instead of here.
        var tooLong = string.Join('.', Enumerable.Repeat("abcdefghij", 26)) + ".com";

        Assert.True(tooLong.Length > 253);
        Assert.False(MailDomainName.IsWellFormed(tooLong));
    }

    [Fact]
    public void A_label_longer_than_dns_allows_is_refused() =>
        Assert.False(MailDomainName.IsWellFormed(new string('a', 64) + ".com"));
}

// The challenge value itself (#667). It is the whole proof — anyone who can guess one can claim a domain they
// do not own — so what matters is that it is unguessable and that it says what it is for.
public class MailDomainChallengeTests
{
    [Fact]
    public void A_token_names_the_product_and_carries_real_randomness()
    {
        var token = MailDomainChallenge.NewToken();

        // Named, so a zone full of verification records from a dozen services stays readable — and so nobody
        // is afraid to delete it years later because it is an anonymous opaque string.
        Assert.StartsWith("simplarchive-domain-verification=", token, StringComparison.Ordinal);

        // 20 bytes, base64url, unpadded → 27 characters.
        Assert.Equal(27, token["simplarchive-domain-verification=".Length..].Length);

        // URL- and DNS-safe: a TXT value containing '+' or '/' survives, but is the kind of thing a zone editor
        // or a copy-paste mangles, and a challenge that fails to verify because of punctuation is unanswerable.
        // Asked of the VALUE, not the whole token — the prefix ends in '=' by construction, which is what
        // makes the record read as a key/value pair.
        var value = token["simplarchive-domain-verification=".Length..];
        Assert.DoesNotContain('+', value);
        Assert.DoesNotContain('/', value);
        Assert.DoesNotContain('=', value);
    }

    [Fact]
    public void Two_tokens_are_never_the_same()
    {
        // Not a strength test — a loop cannot prove randomness. It catches the one failure that would matter
        // and is easy to introduce: a token derived from something constant, or cached.
        var tokens = Enumerable.Range(0, 200).Select(_ => MailDomainChallenge.NewToken()).ToHashSet();

        Assert.Equal(200, tokens.Count);
    }
}
