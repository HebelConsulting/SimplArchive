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

    // A proxy in front terminates IMAPS and the app binds NOTHING (#1268). The old gate asked "did I bind a
    // TLS listener", so with Caddy terminating, the dialog said plaintext only while 993 answered perfectly —
    // the server accepting more than it advertised. The clients that need 993 fail SILENTLY (Apple's Internet
    // Accounts refuses plaintext IMAP with no error), so nobody reports it; the account just never syncs.
    [Fact]
    public void A_TLS_port_terminated_in_front_is_advertised_even_though_nothing_is_bound()
    {
        Assert.Equal(993, new ImapOptions { TlsPort = 0, ExternalTlsPort = 993 }.AdvertisedTlsPort);
    }

    // Kept SEPARATE from PublicTlsPort rather than relaxing its gate, because the two state different facts and
    // one field meaning both cannot be read without knowing the deployment — the #682 ambiguity. This pins that
    // the kiosk's mapping case is untouched: it binds 9993 and publishes 993, and still advertises 993.
    [Fact]
    public void The_port_mapping_case_is_unchanged_by_the_new_setting()
    {
        Assert.Equal(993, new ImapOptions { TlsPort = 9993, PublicTlsPort = 993 }.AdvertisedTlsPort);

        // And PublicTlsPort still advertises NOTHING on its own — a mapping for a listener that does not exist
        // is a contradiction, and answering it would be guessing.
        Assert.Null(new ImapOptions { TlsPort = 0, PublicTlsPort = 993 }.AdvertisedTlsPort);
    }

    // Both set is a deployment that binds TLS *and* sits behind a terminating proxy. The OUTERMOST port is the
    // one a user can actually dial, so the external one wins — and it is asserted rather than left to the
    // reading order of a null-coalescing chain.
    [Fact]
    public void The_outermost_port_wins_when_a_deployment_states_both()
    {
        Assert.Equal(993, new ImapOptions { TlsPort = 9993, PublicTlsPort = 8993, ExternalTlsPort = 993 }.AdvertisedTlsPort);
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
