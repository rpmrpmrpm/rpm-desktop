using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RPM.Desktop.Services.Updates;

/// <summary>
/// The signature on an update feed.
///
/// <para>Each release writes <c>releases.&lt;channel&gt;.json</c>, the Velopack feed. Beside it, it writes
/// <see cref="FileName"/>: the same bytes with the channel and an ECDSA P-256 signature, in one file. The
/// app reads only the signed file (<c>SignedWebSource</c>), and it refuses the file unless the signature
/// verifies against <see cref="PublicKey"/>. The feed holds each package's size and SHA-256, which Velopack
/// checks after every download. So the signature covers the packages too. Without it, whoever controlled
/// the update host could offer any package.</para>
///
/// <para>The feed and its signature are one file, not two. So an app never reads a new feed together with
/// the old signature, which can happen while the two are being uploaded or cached. The channel is signed
/// together with the feed, so one architecture's feed cannot be served to another.</para>
///
/// <para>The private key is kept in the release secrets store as <c>RPM/UpdateFeedKey</c> (PKCS#8 PEM).
/// Both release paths give it to <c>tools/FeedSign</c> through a pipe. That tool signs with this class, and
/// it checks the result against <see cref="PublicKey"/> before anything is uploaded. This file is compiled
/// into that tool as well, so it uses only the runtime and names nothing else in the app.</para>
/// </summary>
public static class FeedSignature
{
    /// <summary>The public half of <c>RPM/UpdateFeedKey</c>: SubjectPublicKeyInfo, base64. Changing this
    /// key needs care. Copies of the app that hold the old key refuse every feed signed with the new one.
    /// So a key change ships in a release that is signed with the old key and carries the new public
    /// key.</summary>
    public const string PublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAExeGLVsfc8noYw5GP2+rjKE3eLj/+2t83A04+SkCq0uj16cH3Bvb9r1ZSU2MXoLBcbURqMiXcMiQYdNxFdHKREg==";

    /// <summary>Placed before the channel and the feed in the signed bytes. So a signature made for this
    /// purpose cannot be reused for anything else.</summary>
    private const string Label = "rpm-update-feed";

    /// <summary>The signed file for a channel, beside the feed on the update host.</summary>
    public static string FileName(string channel) => $"releases.{channel}.signed.json";

    /// <summary>The signed file's contents for one channel's feed.</summary>
    public static string Sign(string channel, byte[] feed, ECDsa key)
    {
        ArgumentException.ThrowIfNullOrEmpty(channel);
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(key);

        var signature = key.SignData(
            Signed(channel, feed), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("channel", channel);
            writer.WriteString("feed", Convert.ToBase64String(feed));
            writer.WriteString("signature", Convert.ToBase64String(signature));
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// The feed inside a signed file, once the file is shown to be for <paramref name="channel"/> and to be
    /// signed with the key whose public half is <paramref name="publicKey"/>.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is not a signed feed, is for another channel, or its
    /// signature does not verify. The message says which.</exception>
    public static byte[] Open(string signedFile, string channel, byte[] publicKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(channel);
        ArgumentNullException.ThrowIfNull(publicKey);

        string? named, feedText, signatureText;
        try
        {
            using var document = JsonDocument.Parse(signedFile ?? "");
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
                throw new InvalidDataException("The signed feed is not a JSON object.");

            named = Text(document.RootElement, "channel");
            feedText = Text(document.RootElement, "feed");
            signatureText = Text(document.RootElement, "signature");
        }
        catch (JsonException)
        {
            throw new InvalidDataException("The signed feed is not JSON.");
        }

        if (named != channel)
            throw new InvalidDataException($"The signed feed is for {named ?? "no channel"}, not {channel}.");

        if (Bytes(feedText) is not { } feed || Bytes(signatureText) is not { Length: > 0 } signature)
            throw new InvalidDataException("The signed feed is missing the feed or the signature.");

        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(publicKey, out _);

        if (!key.VerifyData(
                Signed(channel, feed), signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new InvalidDataException($"The signature on the {channel} feed does not verify.");

        return feed;
    }

    private static byte[] Signed(string channel, byte[] feed) =>
        [.. Encoding.UTF8.GetBytes($"{Label}\n{channel}\n"), .. feed];

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String ? value.GetString() : null;

    private static byte[]? Bytes(string? base64)
    {
        if (base64 is null)
            return null;

        try { return Convert.FromBase64String(base64); }
        catch (FormatException) { return null; }
    }
}
