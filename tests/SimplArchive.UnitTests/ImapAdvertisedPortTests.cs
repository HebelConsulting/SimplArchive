using SimplArchive.Api.Imap;

namespace SimplArchive.UnitTests;

// Which port the IMAP dialog shows (#682).
//
// The bug this pins was one wrong number, and the worst shape of documentation bug: confidently specific and
// wrong. A container bound 9993 and was published as `993:9993`, and the dialog advertised what it BOUND — so
// every user following the instruction reached a port nothing outside can open, and the failure looked like a
// broken server. PublicHost already existed for exactly this split; the ports had been left behind.
public class ImapAdvertisedPortTests
{
    // The deployment that remaps — the kiosk's case, and the one that was wrong.
    [Fact]
    public void The_published_port_wins_over_the_bound_one()
    {
        var options = new ImapOptions { TlsPort = 9993, PublicTlsPort = 993 };
        Assert.Equal(993, options.AdvertisedTlsPort);
    }

    // The ordinary deployment publishes what it binds and configures nothing. Falling back rather than
    // defaulting to 993 is deliberate: an unset value meaning "the standard port" would be a guess about
    // somebody else's mapping, stated as confidently as the bug was.
    [Fact]
    public void An_unset_public_port_falls_back_to_the_bound_one()
    {
        Assert.Equal(9993, new ImapOptions { TlsPort = 9993 }.AdvertisedTlsPort);
    }

    // Off is off: nothing to dial, so nothing is shown — a port of 0 must not surface as "0".
    [Fact]
    public void A_disabled_port_advertises_nothing()
    {
        Assert.Null(new ImapOptions { TlsPort = 0, PublicTlsPort = 993 }.AdvertisedTlsPort);
        Assert.Null(new ImapOptions { Port = 0, PublicPort = 143 }.AdvertisedPort);
    }

    // The plaintext port has the same split and the same trap; it is only ever used in development, which is
    // exactly the sort of path that gets the fix later and the bug for longer.
    [Fact]
    public void The_plaintext_port_behaves_the_same_way()
    {
        Assert.Equal(143, new ImapOptions { Port = 1143, PublicPort = 143 }.AdvertisedPort);
        Assert.Equal(1143, new ImapOptions { Port = 1143 }.AdvertisedPort);
    }

    // An ephemeral bind (-1, what the tests use) resolves to a real port at listen time. Advertising -1 would
    // be nonsense, but so would hiding it: the value is simply whatever was configured, and a deployment that
    // binds ephemerally has to say what it publishes.
    [Fact]
    public void An_ephemeral_bind_still_advertises_what_it_publishes()
    {
        Assert.Equal(993, new ImapOptions { TlsPort = -1, PublicTlsPort = 993 }.AdvertisedTlsPort);
    }
}
