using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MimeKit;
using MimeKit.Cryptography;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// Opens the CMS envelope a strict-tier read arrives in, using a key this machine can reach (#1353, ADR 0830).
/// </summary>
/// <remarks>
/// <para>
/// A strict tenant never serves readable content: <c>download</c> and <c>preview</c> return
/// <c>application/pkcs7-mime</c> addressed to the reader's registered certificate. Everything this client does
/// with a document — render it, open it in its real application, drag it to the desktop, rasterise its
/// thumbnails — starts from those same bytes, so opening the envelope belongs at the ONE content funnel they
/// all pass through rather than at each of them.
/// </para>
/// <para>
/// <b>An ordinary read is untouched.</b> Anything that is not a pkcs7-mime payload is returned exactly as it
/// arrived, so a non-strict tenant pays nothing and this cannot half-apply.
/// </para>
/// <para>
/// <b>Openers are tried IN ORDER, and the order is the design</b> (ADR 0832). Software first: it costs a
/// certificate-store read, needs no PIN and no hardware, and it is the case a machine with an installed
/// identity is in. The card second, because reaching it means a PIN prompt — asking for one before trying the
/// free thing would make every read on an ordinary machine demand hardware it does not need.
/// </para>
/// <para>
/// Each opener answers <c>null</c> to DECLINE — "this envelope is not addressed to anything I hold" — and
/// throws only when it recognises the envelope as its own and still cannot open it. That distinction is what
/// lets the list fall through cleanly while a real failure still reaches the user with its reason.
/// </para>
/// </remarks>
public static class EnvelopeOpener
{
    /// <summary>One way of opening an envelope: the document and its type, or null to decline.</summary>
    /// <param name="enveloped">The CMS envelope as it arrived.</param>
    /// <param name="recipients">
    /// Who it is addressed to, read ONCE by the funnel from public data. Every opener needs this before it
    /// reaches for a key, so it is computed in one place rather than re-derived per opener.
    /// </param>
    /// <remarks>
    /// ASYNCHRONOUS because of exactly one opener: the card may have to ask for a PIN, and a PIN prompt is a
    /// modal dialog. This runs on the UI thread (the funnel's await resumes there), so blocking on
    /// <c>ShowDialog</c> would deadlock the very thread that has to draw it. The software opener does no I/O
    /// and simply completes.
    /// </remarks>
    public delegate Task<(byte[] Bytes, string ContentType)?> Opener(
        ApplicationPkcs7Mime enveloped, IReadOnlyList<(string Issuer, string Serial)> recipients);

    /// <summary>Where the software private keys come from. Replaced in tests.</summary>
    public static Func<X509Certificate2Collection> Keys { get; set; } = FromUserStore;

    /// <summary>The openers, tried in order. Replaced in tests; the card is the second by default.</summary>
    public static IReadOnlyList<Opener> Openers { get; set; } = [OpenWithSoftwareKey, CardEnvelopeOpener.TryOpen];

    /// <summary>
    /// Returns the document inside the envelope and its real content type, or the input unchanged.
    /// </summary>
    /// <remarks>
    /// The INNER content type is returned, because that is what the caller renders: the outer
    /// <c>application/pkcs7-mime</c> describes the wrapper, and a PDF announced as pkcs7-mime would be
    /// sniffed, mis-rendered or refused by every consumer downstream.
    /// </remarks>
    public static async Task<(byte[] Bytes, string ContentType)> OpenAsync(byte[] served, string contentType)
    {
        if (!contentType.Contains("pkcs7-mime", StringComparison.OrdinalIgnoreCase))
        {
            return (served, contentType);
        }

        var message = MimeMessage.Load(new MemoryStream(served));
        if (message.Body is not ApplicationPkcs7Mime enveloped)
        {
            // Labelled as an envelope and shaped like something else. Refused rather than guessed at: handing
            // the caller bytes we could not open would make a decryption failure look like a corrupt document.
            throw new EnvelopeNotOpenedException("the response was labelled as an envelope but does not contain one");
        }

        // WHO IS THIS FOR — read once, from public data, before any opener reaches for a key. Both openers
        // need the answer and both used to work it out themselves, which meant decoding the same CMS twice and,
        // worse, two separate places that could drift on what counts as a match.
        var recipients = EnvelopeRecipients.Of(enveloped);

        foreach (var opener in Openers)
        {
            if (await opener(enveloped, recipients) is { } opened)
            {
                return opened;
            }
        }

        // Every opener declined, so the remedy depends on WHICH of the three things is missing — and saying
        // "this is not yours" when the reader simply has no reader plugged in sends them to their
        // administrator instead of to their pocket (#1353's acceptance point 3).
        throw new EnvelopeNotOpenedException(CardEnvelopeOpener.WhatIsMissing());
    }

    /// <summary>The user's own certificate store — the platform path, proven before any token is involved.</summary>
    /// <remarks>
    /// Only certificates WITH a private key: the public half is what gets registered with the server, and a
    /// store full of public certificates would make every failure look like a wrong-recipient failure.
    /// </remarks>
    private static X509Certificate2Collection FromUserStore()
    {
        var found = new X509Certificate2Collection();
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly);
            foreach (var certificate in store.Certificates)
            {
                if (certificate.HasPrivateKey)
                {
                    found.Add(certificate);
                }
            }
        }
        catch (CryptographicException)
        {
            // A platform with no usable store is not an error here — it is the case the card reader exists
            // for, and the caller's message already names what is missing.
        }

        return found;
    }

    /// <summary>The software opener: MimeKit over the certificates this machine holds with their private keys.</summary>
    private static Task<(byte[] Bytes, string ContentType)?> OpenWithSoftwareKey(
        ApplicationPkcs7Mime enveloped, IReadOnlyList<(string Issuer, string Serial)> recipients) =>
        Task.FromResult(SoftwareKey(enveloped, recipients));

    private static (byte[] Bytes, string ContentType)? SoftwareKey(
        ApplicationPkcs7Mime enveloped, IReadOnlyList<(string Issuer, string Serial)> recipients)
    {
        // ONLY the certificates this envelope is actually addressed to, decided BEFORE any private key is
        // touched. A CMS envelope names its recipients by issuer and serial in the clear, so "is this mine?"
        // is answerable with no key material at all.
        //
        // WHY THAT MATTERS, and it is not an optimisation. MimeKit's context needs the PRIVATE KEY to build a
        // decryptor, so importing a certificate asks the platform to hand that key over — and on macOS that
        // means a keychain authorization prompt. Importing everything and asking afterwards therefore made a
        // document addressed to a CARD raise prompts for unrelated software certificates before the card was
        // ever consulted. Asking the envelope first means a card-addressed document touches the keychain not
        // at all.
        var candidates = EnvelopeRecipients.AddressedTo(recipients, Keys());
        if (candidates.Count == 0)
        {
            return null;
        }

        using var context = new TemporarySecureMimeContext();
        var imported = 0;
        foreach (var certificate in candidates)
        {
            try
            {
                context.Import(certificate);
                imported++;
            }
            catch (Exception e)
            {
                // A certificate the platform will not hand over. OBSERVED on macOS: importing a keychain
                // identity raises "Unable to obtain authorization for this operation" from Security.framework,
                // intermittently — and before this guard that exception escaped the whole opener chain, so a
                // machine that could have read the document WITH ITS CARD failed outright because an unrelated
                // software certificate refused to be exported.
                //
                // Still guarded even though only addressees are imported now: the user's OWN certificate can
                // refuse just as easily, and then the card is still the way in.
                DesktopLog.Debug("Skipping certificate {Subject}: {Reason}", certificate.Subject, e.Message);
            }
        }

        if (imported == 0)
        {
            return null;
        }

        MimeEntity decrypted;
        try
        {
            decrypted = enveloped.Decrypt(context);
        }
        // CmsException is matched BY NAME, and that is deliberate. MimeKit decrypts through BouncyCastle, which
        // reports the commonest real failure — "A suitable private key could not be found for decrypting" — as
        // Org.BouncyCastle.Cms.CmsException rather than a CryptographicException. Naming the type would bind
        // this client to a package it only has transitively, and catching everything would swallow genuine
        // defects and report them to the reader as a missing key. Found by writing the wrong-recipient test
        // and watching it throw the wrong type, which is exactly what that test is for.
        //
        // DECLINING rather than throwing, since the ordered list took over (ADR 0832): "no software key opens
        // this" is precisely the case the card exists for, so it must fall through instead of ending the walk.
        catch (Exception e) when (e is CryptographicException or ArgumentException or FormatException
            || e.GetType().Name == "CmsException")
        {
            return null;
        }

        if (decrypted is not MimePart part || part.Content is null)
        {
            throw new EnvelopeNotOpenedException("the envelope opened but held no document");
        }

        using var opened = new MemoryStream();
        part.Content.DecodeTo(opened);
        return (opened.ToArray(), part.ContentType?.MimeType ?? "application/octet-stream");
    }
}

/// <summary>The envelope could not be opened — never a fall back to whatever arrived.</summary>
/// <remarks>
/// Its own type because the remedy differs from every other download failure: no key on this machine means
/// enrol or insert the card, not retry. Serving the caller the undecrypted bytes would render as a corrupt
/// document and send them looking for a damaged file.
/// </remarks>
public sealed class EnvelopeNotOpenedException(string message, Exception? inner = null)
    : Exception($"This document arrived encrypted and could not be opened: {message}.", inner);
