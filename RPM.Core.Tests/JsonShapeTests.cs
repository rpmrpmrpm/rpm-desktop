using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using RPM.Core.Cloud;
using RPM.Core.Recording;
using RPM.Core.Ripping;
using RPM.Core.Sharing;
using Xunit;

namespace RPM.Core.Tests;

/// <summary>
/// The types that are sent between machines, or stored in files written by older builds, serialise
/// to exactly the same JSON with the source generator as with reflection.
/// </summary>
/// <remarks>
/// <para>The app is published as Native AOT, where reflection-based serialisation is off and every
/// type is described by a generated JsonSerializerContext. The JSON text must not change: a renamed
/// field means a peer that cannot read a catalogue, a sidecar that loses its tags, or a rip manifest
/// that reads back empty.</para>
///
/// <para>So each test serialises the same value twice, once with the generated metadata the app
/// uses and once with the reflection resolver, and compares the text. The test host is not AOT, so
/// the reflection resolver works here.</para>
/// </remarks>
public class JsonShapeTests
{
    /// <summary>The same options with the reflection resolver, for the sharing protocol.</summary>
    private static JsonSerializerOptions Reflected(JsonSerializerOptions like) =>
        new(like) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

    private static void Same<T>(T value, JsonTypeInfo<T> generated, JsonSerializerOptions reflected) =>
        Assert.Equal(
            JsonSerializer.Serialize(value, reflected),
            JsonSerializer.Serialize(value, generated));

    [Fact]
    public void A_peer_track_is_written_exactly_as_it_was()
    {
        var track = new PeerTrack
        {
            RpmId = "abc123",
            RelativePath = "Gary Numan/The Pleasure Principle/03 Cars.flac",
            Title = "Cars",
            Artist = "Gary Numan",
            Album = "The Pleasure Principle",
            DurationSeconds = 237.5,
            TrackNumber = 3,
        };

        Same(track, SharingJson.Default.PeerTrack, Reflected(SharingProtocol.Json));
    }

    [Fact]
    public void A_peer_album_is_written_exactly_as_it_was()
    {
        var album = new PeerAlbum
        {
            Folder = "Gary Numan/The Pleasure Principle",
            Favorite = true,
            Rating = 4,
            Review = "Still the best of them.",
            ReleaseProvider = "discogs",
            ReleaseId = "12345",
        };

        Same(album, SharingJson.Default.PeerAlbum, Reflected(SharingProtocol.Json));
    }

    [Fact]
    public void A_pair_offer_is_written_exactly_as_it_was()
    {
        var offer = new PairOffer
        {
            DeviceId = "device-1",
            Name = "Front room",
            PublicKey = "AAAA",
            Id = "pairing-1",
            Status = new PairStatus { State = PairStatusKind.ShowingCode, Rev = 3, TriesLeft = 2, ExpiresIn = 90 },
        };

        Same(offer, SharingJson.Default.PairOffer, Reflected(SharingProtocol.Json));
    }

    /// <summary>
    /// The catalogue version is a hash OF the JSON. If the JSON changed, every borrower would be told
    /// the shelf had changed and would download it again.
    /// </summary>
    [Fact]
    public void The_catalog_version_does_not_move_when_the_serializer_changes()
    {
        var tracks = new[]
        {
            new PeerTrack { RpmId = "b", RelativePath = "Numan/One/2.flac", Title = "Second" },
            new PeerTrack { RpmId = "a", RelativePath = "Numan/One/1.flac", Title = "First" },
        };
        var albums = new[] { new PeerAlbum { Folder = "Numan/One", Rating = 5 } };

        var version = PeerCatalog.VersionOf(tracks, albums, [], []);

        // The same computation, written out with the reflection serialiser.
        var reflected = Reflected(SharingProtocol.Json);
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        foreach (var track in tracks.OrderBy(t => t.RpmId, StringComparer.Ordinal))
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(track, reflected)));
            hash.AppendData("\n"u8);
        }
        foreach (var album in albums.OrderBy(a => a.Folder, StringComparer.Ordinal))
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(album, reflected)));
            hash.AppendData("\n"u8);
        }

        Assert.Equal(SharingProtocol.ToBase64Url(hash.GetHashAndReset()[..12]), version);
    }

    [Fact]
    public void A_sidecar_is_written_exactly_as_it_was()
    {
        var sidecar = new TagSidecar
        {
            RelativePath = "Gary Numan/The Pleasure Principle/03 Cars.flac",
            Extension = ".flac",
            Title = "Cars",
            Artist = "Gary Numan",
            Album = "The Pleasure Principle",
            TrackNumber = 3,
            Favorite = true,
        };

        var reflected = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };

        Assert.Equal(JsonSerializer.Serialize(sidecar, reflected), sidecar.ToJson());
    }

    /// <summary>
    /// The manifest writes its enum by name. A published build cannot create a
    /// JsonStringEnumConverter instance, so the generated converter must write the same name.
    /// Otherwise a rip staged by an older build reads back with the wrong setting.
    /// </summary>
    [Fact]
    public void A_rip_manifest_still_names_its_enum_rather_than_numbering_it()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rpm-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var manifest = new RipManifest(
                RipSource.Cd, Title: "The Pleasure Principle", Artist: "Gary Numan", TrackCount: 10);
            manifest.WriteTo(folder);

            var text = File.ReadAllText(Path.Combine(folder, RipManifest.FileName));
            Assert.Contains($"\"{nameof(RipManifest.Source)}\": \"{RipSource.Cd}\"", text, StringComparison.Ordinal);

            var read = RipManifest.Read(folder);
            Assert.NotNull(read);
            Assert.Equal(manifest.Artist, read!.Artist);
            Assert.Equal(manifest.Title, read.Title);
            Assert.Equal(manifest.TrackCount, read.TrackCount);
            Assert.Equal(RipSource.Cd, read.Source);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Restore_settings_survive_a_round_trip_through_the_generated_shape()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rpm-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new RestoreSettings { EqOn = true, Preamp = -3.5, Bass = 2 };
            settings.WriteTo(folder);

            var read = RestoreSettings.Read(folder);
            Assert.NotNull(read);
            Assert.True(read!.EqOn);
            Assert.Equal(-3.5, read.Preamp);
            Assert.Equal(2, read.Bass);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
