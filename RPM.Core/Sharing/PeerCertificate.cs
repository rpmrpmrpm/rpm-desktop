using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RPM.Core.Sharing;

/// <summary>
/// The certificate a machine serves its library under, and the one rule for believing somebody else's.
///
/// <para>
/// No authority is involved, and nothing has to be bought. The certificate is made from the device key
/// itself. So the key inside it <em>is</em> the identity that the peer is trusted by. "Is this the right
/// server?" becomes a comparison against the key already in the book. There is no chain, no expiry and
/// nothing to renew. A name in a certificate would add nothing. Two computers on a home network have no
/// names that anybody issued.
/// </para>
///
/// <para>
/// On a local network, TLS protects the connection itself. The token, the catalogue and the music all
/// cross a network segment that other people may be on. A café network is a segment full of strangers.
/// Pinning the key stops one of them from answering in the library's place.
/// </para>
/// </summary>
public static class PeerCertificate
{
    /// <summary>
    /// Ten years. Nothing checks the date on this certificate. The same install keeps it for as long as
    /// the install exists. An expiry date would be a day when the music stopped for no reason anybody
    /// could see.
    /// </summary>
    private static readonly TimeSpan Life = TimeSpan.FromDays(3650);

    /// <summary>
    /// A self-signed certificate over this install's own key.
    ///
    /// <para>It is exported and loaded again rather than used as built. On Windows, a certificate created
    /// in memory carries a key handle that <see cref="System.Net.Security.SslStream"/> will not serve
    /// with. The PKCS#12 round trip is the documented way to get one that it will serve with.</para>
    /// </summary>
    public static X509Certificate2 For(PeerIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(identity.Export(), out _);

        var request = new CertificateRequest(
            $"CN={identity.DeviceId}", key, HashAlgorithmName.SHA256);

        // Not a certificate authority. It is used for one thing: to prove that the machine at the other
        // end of the socket holds the key that the library is shared with.
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyAgreement, critical: false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], critical: false));   // server authentication

        var now = DateTimeOffset.UtcNow;
        using var made = request.CreateSelfSigned(now.AddDays(-1), now.Add(Life));

        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pkcs12), null);
    }

    /// <summary>
    /// A message handler that believes exactly one certificate: the one that carries this peer's key.
    ///
    /// <para>Used where a peer's bytes are read without going through <see cref="PeerClient"/>. For
    /// example, the phone opens a track as a stream that it passes straight to the audio parser. Those
    /// places pin the same key as everything else. Each one does not invent its own answer.</para>
    /// </summary>
    /// <remarks>
    /// Built through <see cref="RPM.Core.Net.Wire.LocalSockets"/>, which is the only code in this app that
    /// makes a socket handler. The gate does not use this limit: a peer is on this network and must stay
    /// reachable without a route out. The limit is on the number of connections, and that is not about
    /// reaching outside at all. A borrowed shelf is a screenful of covers asked for at once. Without a
    /// limit, one socket would be opened for each cover.
    /// </remarks>
    public static System.Net.Http.HttpMessageHandler Handler(byte[] publicKey) =>
        RPM.Core.Net.Wire.LocalSockets(
            ssl: new System.Net.Security.SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, _) => Matches(certificate, publicKey),
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            },

            // A computer on the same network answers a connection in milliseconds. A computer that has been
            // shut down does not answer at all. A beacon can outlive the app that sent it. So this timeout
            // is the difference between "that machine is not there" arriving in three seconds, and arriving
            // after the whole request deadline. That late arrival is what a record that hangs and then
            // silently fails to play really is.
            connectTimeout: ConnectWait);

    /// <summary>How long a machine on the same network gets to accept a socket. This is not the time a
    /// request gets. That is the caller's choice, and a catalogue request may take minutes.</summary>
    public static readonly TimeSpan ConnectWait = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Whether a certificate presented on the wire belongs to the key a peer is trusted by.
    ///
    /// <para>This is all the certificate validation there is. The name, the chain and the dates say
    /// nothing about a computer in the next room. The key does say something. The six digits were
    /// computed over the key, and the trust book was written against it.</para>
    /// </summary>
    public static bool Matches(X509Certificate? presented, byte[]? expectedPublicKey)
    {
        if (presented is null || expectedPublicKey is not { Length: > 0 })
            return false;

        try
        {
            using var certificate = X509CertificateLoader.LoadCertificate(presented.GetRawCertData());
            return certificate.PublicKey.ExportSubjectPublicKeyInfo().AsSpan()
                .SequenceEqual(expectedPublicKey);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
