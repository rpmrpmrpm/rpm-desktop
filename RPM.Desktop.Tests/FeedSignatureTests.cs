using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using RPM.Desktop.Services.Updates;

using Velopack.Logging;
using Velopack.Sources;

namespace RPM.Desktop.Tests;

/// <summary>
/// The signed update feed: what opens, what is refused, and that the app's source reads only the signed
/// file.
/// </summary>
public class FeedSignatureTests : IDisposable
{
    private const string Channel = "osx-arm64";

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public void Dispose() => _key.Dispose();

    private byte[] PublicKey => _key.ExportSubjectPublicKeyInfo();

    private static byte[] Feed(string sha256 = "2ACBDEE283D4D005B73358EB61C2E313E328C6866EB485927FE5730BC4DDEF73") =>
        Encoding.UTF8.GetBytes(
            "{\"Assets\":[{\"PackageId\":\"com.rpm.desktop\",\"Version\":\"0.1.11\",\"Type\":\"Full\"," +
            "\"FileName\":\"com.rpm.desktop-0.1.11-osx-arm64-full.nupkg\"," +
            "\"SHA1\":\"15B37D8CEE8F9F3DD66A79976A68043F2F8C1F1C\"," +
            $"\"SHA256\":\"{sha256}\",\"Size\":535075737}}]}}");

    [Fact]
    public void A_signed_feed_opens_with_its_key_and_gives_back_the_same_bytes()
    {
        var signed = FeedSignature.Sign(Channel, Feed(), _key);

        Assert.Equal(Feed(), FeedSignature.Open(signed, Channel, PublicKey));
    }

    [Fact]
    public void A_feed_changed_after_signing_is_refused()
    {
        var file = JsonNode.Parse(FeedSignature.Sign(Channel, Feed(), _key))!;
        var changed = Feed();
        changed[^3] = (byte)'8';
        file["feed"] = Convert.ToBase64String(changed);

        var refused = Assert.Throws<InvalidDataException>(
            () => FeedSignature.Open(file.ToJsonString(), Channel, PublicKey));
        Assert.Contains("does not verify", refused.Message);
    }

    [Fact]
    public void A_feed_signed_for_another_channel_is_refused()
    {
        var signed = FeedSignature.Sign("osx-x64", Feed(), _key);

        Assert.Throws<InvalidDataException>(() => FeedSignature.Open(signed, Channel, PublicKey));
    }

    /// <summary>The channel is inside the signature. So renaming it in the file does not make another
    /// architecture's feed open here.</summary>
    [Fact]
    public void A_channel_renamed_in_the_file_is_refused()
    {
        var file = JsonNode.Parse(FeedSignature.Sign("osx-x64", Feed(), _key))!;
        file["channel"] = Channel;

        var refused = Assert.Throws<InvalidDataException>(
            () => FeedSignature.Open(file.ToJsonString(), Channel, PublicKey));
        Assert.Contains("does not verify", refused.Message);
    }

    [Fact]
    public void A_feed_signed_with_another_key_is_refused()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = FeedSignature.Sign(Channel, Feed(), other);

        Assert.Throws<InvalidDataException>(() => FeedSignature.Open(signed, Channel, PublicKey));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"Assets\":[]}")]
    [InlineData("{\"channel\":\"osx-arm64\",\"feed\":\"e30=\"}")]
    [InlineData("{\"channel\":\"osx-arm64\",\"feed\":\"e30=\",\"signature\":\"not base64!\"}")]
    [InlineData("{\"channel\":\"osx-arm64\",\"feed\":\"e30=\",\"signature\":\"AAAA\"}")]
    public void Anything_that_is_not_a_signed_feed_is_refused(string file)
    {
        Assert.Throws<InvalidDataException>(() => FeedSignature.Open(file, Channel, PublicKey));
    }

    [Fact]
    public void The_key_the_app_checks_with_is_a_P256_public_key()
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(FeedSignature.PublicKey), out var read);

        Assert.Equal(256, key.KeySize);
        Assert.Equal(Convert.FromBase64String(FeedSignature.PublicKey).Length, read);
    }

    // ── the app's source ──

    /// <summary>Answers every request for a file with <see cref="Answer"/>, and keeps the addresses
    /// asked for.</summary>
    private sealed class Host(string answer) : IFileDownloader
    {
        public string Answer { get; } = answer;
        public List<string> Asked { get; } = [];

        public Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30)
        {
            Asked.Add(url);
            return Task.FromResult(Answer);
        }

        public Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30) =>
            throw new InvalidOperationException("the source reads the feed as text");

        public Task DownloadFile(string url, string targetFile, Action<int> progress,
            IDictionary<string, string>? headers = null, double timeout = 30, CancellationToken cancelToken = default) =>
            throw new InvalidOperationException("no package is downloaded here");
    }

    private SignedWebSource Source(Host host) =>
        new($"https://updates.rpmrpmrpm.com/{Channel}/", PublicKey, host);

    [Fact]
    public async Task The_source_reads_the_signed_file_and_hands_Velopack_its_feed()
    {
        var host = new Host(FeedSignature.Sign(Channel, Feed(), _key));

        var feed = await Source(host).GetReleaseFeed(NullVelopackLogger.Instance, null, Channel);

        Assert.Equal([$"https://updates.rpmrpmrpm.com/{Channel}/releases.{Channel}.signed.json"], host.Asked);
        var asset = Assert.Single(feed.Assets);
        Assert.Equal("0.1.11", asset.Version.ToString());
        Assert.Equal(535075737, asset.Size);
    }

    [Fact]
    public async Task The_source_refuses_a_feed_whose_signature_does_not_verify()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var host = new Host(FeedSignature.Sign(Channel, Feed(), other));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => Source(host).GetReleaseFeed(NullVelopackLogger.Instance, null, Channel));
    }

    [Fact]
    public async Task The_source_refuses_the_plain_feed()
    {
        var host = new Host(Encoding.UTF8.GetString(Feed()));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => Source(host).GetReleaseFeed(NullVelopackLogger.Instance, null, Channel));
    }

    /// <summary>Velopack checks an entry that has no SHA-256 by its SHA-1. A signed feed never has such an
    /// entry. So it is refused, rather than checked by the weaker method.</summary>
    [Fact]
    public async Task The_source_refuses_a_signed_feed_with_an_entry_without_SHA256()
    {
        var host = new Host(FeedSignature.Sign(Channel, Feed(sha256: ""), _key));

        var refused = await Assert.ThrowsAsync<InvalidDataException>(
            () => Source(host).GetReleaseFeed(NullVelopackLogger.Instance, null, Channel));
        Assert.Contains("SHA-256", refused.Message);
    }
}
