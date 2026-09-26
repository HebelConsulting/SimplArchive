using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MimeKit;
using MimeKit.Cryptography;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// Opens the CMS envelope a strict-tier read arrives in, using a key this machine holds (#1353, ADR 0828).
/// </summary>
/// <remarks>
/// <para>
/// A strict tenant never serves readable content: <c>download</c> and <c>preview</c> return
/// <c>application/pkcs7-mime</c> addressed to the reader's registered certificate. Everything this client does
/// with a document — render it, open it in its real application, drag it to the desktop, rasterise its
/// thumbnails — starts from those same bytes, so opening the envelope belongs at the ONE content funnel they
/// all pass through rather than at each of them (the consolidation #1381 made for its own reasons).
/// </para>
/// <para>
/// <b>An ordinary read is untouched.</b> Anything that is not a pkcs7-mime payload is returned exactly as it
/// arrived, so a non-strict tenant pays nothing and this cannot half-apply.
/// </para>
/// <para>
/// <b>The key source is deliberately abstracted, and the first one is software.</b> The issue's acceptance
/// criteria are explicit that a SOFTWARE certificate must be proven first: doing the card first means a
/// failure has two candidate causes — "can .NET decrypt from a store at all" and "can .NET use a
/// token-backed key" — and no way to tell them apart. The card (PKCS#11) is the second source and slots in
/// here without any caller changing.
/// </para>
/// </remarks>
public static class EnvelopeOpener
{
    /// <summary>Where the private keys come from. Replaced in tests, and by the card reader later.</summary>
    public static Func<X509Certificate2Collection> Keys { get; set; } = FromUserStore;

    /// <summary>
    /// Returns the document inside the envelope and its real content type, or the input unchanged.
    /// </summary>
    /// <remarks>
    /// The INNER content type is returned, because that is what the caller renders: the outer
    /// <c>application/pkcs7-mime</c> describes the wrapper, and a PDF announced as pkcs7-mime would be
    /// sniffed, mis-rendered or refused by every consumer downstream.
    /// </remarks>
    public static (byte[] Bytes, string ContentType) Open(byte[] served, string contentType)
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

        using var context = new TemporarySecureMimeContext();
        var keys = Keys();
        if (keys.Count == 0)
        {
            throw new EnvelopeNotOpenedException(
                "no certificate with a private key was found on this machine, so the document cannot be opened");
        }

        foreach (var certificate in keys)
        {
            // Every candidate is offered: a user may hold several certificates, and only the one this
            // document was addressed to can open it. Which that is, is the envelope's business, not ours.
            context.Import(certificate);
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
        catch (Exception e) when (e is CryptographicException or ArgumentException or FormatException
            || e.GetType().Name == "CmsException")
        {
            throw new EnvelopeNotOpenedException(
                "this document was addressed to a certificate this machine does not hold the key for", e);
        }

        if (decrypted is not MimePart part || part.Content is null)
        {
            throw new EnvelopeNotOpenedException("the envelope opened but held no document");
        }

        using var opened = new MemoryStream();
        part.Content.DecodeTo(opened);
        return (opened.ToArray(), part.ContentType?.MimeType ?? "application/octet-stream");
    }

    /// <summary>
    /// The user's own certificate store — the platform path, proven before any token is involved.
    /// </summary>
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
}

/// <summary>The envelope could not be opened — never a fall back to whatever arrived.</summary>
/// <remarks>
/// Its own type because the remedy differs from every other download failure: no key on this machine means
/// enrol or insert the card, not retry. Serving the caller the undecrypted bytes would render as a corrupt
/// document and send them looking for a damaged file.
/// </remarks>
public sealed class EnvelopeNotOpenedException(string message, Exception? inner = null)
    : Exception($"This document arrived encrypted and could not be opened: {message}.", inner);
