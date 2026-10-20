using System.Security.Cryptography;
using System.Text;

namespace RPM.Core.Sharing;

/// <summary>
/// What one install of RPM is, to another one.
///
/// <para>A key pair, made once and kept for the life of the install. This is not an account. RPM works
/// signed out, and so does sharing. So the thing that two machines recognise each other by has to be
/// something the machine has, not something a person signed into. An account, when there is one, is bound
/// to this key afterwards. It never replaces the key.</para>
///
/// <para>The key is ECDSA on P-256 because the framework includes it. Ed25519 would be the modern choice,
/// but .NET does not have it. Adding a dependency for a signature that only our own other end verifies
/// gains nothing.</para>
///
/// <para>The <see cref="DeviceId"/> is derived from the public key and is not stored beside it. So a
/// machine cannot claim an id that is not its own: the id is the key, shortened. It stays the same when
/// the machine is renamed, when its address changes, and when someone signs out and signs in as somebody
/// else.</para>
/// </summary>
public sealed class PeerIdentity : IDisposable
{
    /// <summary>Bytes of SHA-256 kept for the device id. Sixteen bytes is 128 bits of a hash that nobody can
    /// grind. A collision would have to be found against a specific machine's key. The id is only a label
    /// on a key, and the key is checked separately.</summary>
    private const int IdBytes = 16;

    private readonly ECDsa _key;

    private PeerIdentity(ECDsa key, byte[] publicKey)
    {
        _key = key;
        PublicKey = publicKey;
        DeviceId = IdFor(publicKey);
    }

    /// <summary>This install's public key, as SubjectPublicKeyInfo. What goes out in the beacon and what a
    /// peer's trust is written against.</summary>
    public byte[] PublicKey { get; }

    /// <summary>The short, stable name for this install, derived from <see cref="PublicKey"/>.</summary>
    public string DeviceId { get; }

    /// <summary>Makes a fresh identity. Called once, the first time an install is asked to share.</summary>
    public static PeerIdentity Create()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new PeerIdentity(key, key.ExportSubjectPublicKeyInfo());
    }

    /// <summary>Reads an identity back from what <see cref="Export"/> wrote.</summary>
    /// <exception cref="CryptographicException">The blob is not a private key we can use.</exception>
    public static PeerIdentity Import(byte[] privateKey)
    {
        ArgumentNullException.ThrowIfNull(privateKey);

        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(privateKey, out _);
        return new PeerIdentity(key, key.ExportSubjectPublicKeyInfo());
    }

    /// <summary>The private key, for the one file that is allowed to hold it. It never leaves the
    /// machine.</summary>
    public byte[] Export() => _key.ExportPkcs8PrivateKey();

    /// <summary>Signs a challenge from a peer. This is the only thing that proves this install is the one
    /// whose key a peer trusts.</summary>
    public byte[] Sign(ReadOnlySpan<byte> data) => _key.SignData(data, HashAlgorithmName.SHA256);

    /// <summary>Whether <paramref name="signature"/> over <paramref name="data"/> was made by the holder of
    /// <paramref name="publicKey"/>. False for anything malformed. A key that cannot be read is a peer that
    /// cannot be believed. It is not an exception for every call site to handle.</summary>
    public static bool Verify(byte[] publicKey, ReadOnlySpan<byte> data, byte[] signature)
    {
        if (publicKey is null || signature is null)
            return false;

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKey, out _);
            return key.VerifyData(data, signature, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>The device id that a public key implies. It is public so that a peer's id can be checked
    /// against the key it actually presented. It must not be taken from what the peer said its id was.</summary>
    public static string IdFor(byte[] publicKey)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        return Base32.Encode(SHA256.HashData(publicKey).AsSpan(0, IdBytes));
    }

    public void Dispose() => _key.Dispose();
}

/// <summary>
/// Base32 with the Crockford alphabet, without padding. Used for short ids that end up in a DNS TXT
/// record, a file name, and sometimes in front of a person. The alphabet leaves out I, L, O and U, so
/// no two characters are easily mistaken for each other. The output is upper case.
/// </summary>
internal static class Base32
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var text = new StringBuilder((data.Length * 8 + 4) / 5);

        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                text.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
            text.Append(Alphabet[(buffer << (5 - bits)) & 31]);

        return text.ToString();
    }
}
