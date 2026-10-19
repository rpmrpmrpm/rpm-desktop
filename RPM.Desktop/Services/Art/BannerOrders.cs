using System.Text.Json;
using System.Text.Json.Serialization;

using RPM.Desktop.Services.Platform;

namespace RPM.Desktop.Services.Art;

/// <summary>One artist's photographs as the home screen's banner wants them.</summary>
/// <param name="Urls">Best first, for the banner's own shape.</param>
/// <param name="Chosen">The photograph that actually arrived and was drawn, if there is one. It is kept
/// apart from the order because the two answer different questions. The order is a judgement about shape.
/// This is a fact about what this machine could fetch.</param>
/// <param name="Ranked">Whether <see cref="BackdropPreselector"/> produced this order. False means it did
/// not. That happens when nothing can be measured, or when the ranking failed. The order is then whatever
/// the sources or the player's saved order gave. This is kept so that a re-rank is not tried on every
/// opening of a screen for an artist whose pictures cannot be measured from here.</param>
public sealed record BannerOrder(IReadOnlyList<string> Urls, string? Chosen, bool Ranked);

/// <summary>
/// The banner's own order of an artist's photographs, kept on disk.
///
/// <para><b>Why not the artist_art table.</b> That order belongs to the player and the user. The now playing
/// header ranks it for its own strip. A person can also arrange it by hand in the carousel. From then on,
/// the automatic ranker must leave it alone (see <c>ArtistArtOrder.Curated</c>). The banner asks a different
/// question of the same photographs: a plate that is much wider than it is tall, with a cover and a title
/// down its left side. Writing this answer into that table would let one surface's taste overwrite
/// another's, and a rank would fight a curation.</para>
///
/// <para><b>Why it is written down at all.</b> Ranking costs a download for each candidate, and a model run
/// over the widest few. If it were held only in memory, it would be paid again on every launch, for a strip
/// that is the first thing on the screen.</para>
///
/// <para>This is a sidecar file beside the app's other small stores (settings, radio, peers). It is read whole
/// and written whole, under one lock. It holds one entry per artist the banner has featured, which is a
/// handful.</para>
/// </summary>
internal static partial class BannerOrders
{
    /// <summary>What the stored orders were produced by. Increase this when the ranking changes in a way that
    /// should reach artists already ranked. Everything older is thrown away and ranked again.</summary>
    private const int Version = 1;

    /// <summary>Where the file lives. It can be set, so that tests write somewhere of their own. The
    /// alternative would be a test that edits the user's own store.</summary>
    internal static string Dir { get; set; } = LocalData.Dir;

    private static string Path => System.IO.Path.Combine(Dir, "banner-art.json");

    private static readonly object Gate = new();

    private sealed record Stored(int Version, Dictionary<string, BannerOrder> Artists);

    /// <summary>What is kept for this artist, or null if nothing is kept. This includes the case where what is
    /// kept was written by an older ranking.</summary>
    public static BannerOrder? For(string artist)
    {
        if (string.IsNullOrWhiteSpace(artist))
            return null;

        lock (Gate)
            return Read().Value?.Artists.GetValueOrDefault(Key(artist));
    }

    /// <summary>Writes down the order the banner will walk through for this artist.</summary>
    public static void Keep(string artist, IReadOnlyList<string> urls, bool ranked)
    {
        if (string.IsNullOrWhiteSpace(artist) || urls.Count == 0)
            return;

        lock (Gate)
        {
            // Not written after a read that failed: it would replace every other artist's order.
            if (Read() is not { MayWrite: true, Value: { } stored })
                return;

            var was = stored.Artists.GetValueOrDefault(Key(artist));
            stored.Artists[Key(artist)] = new BannerOrder(urls, was?.Chosen, ranked);
            Write(stored);
        }
    }

    /// <summary>Writes down the photograph that actually arrived, so the next opening goes straight to it
    /// rather than walking the order again.</summary>
    public static void Chose(string artist, string url)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(url))
            return;

        lock (Gate)
        {
            // As in Keep: not written after a read that failed.
            if (Read() is not { MayWrite: true, Value: { } stored })
                return;

            var was = stored.Artists.GetValueOrDefault(Key(artist));
            if (was?.Chosen == url)
                return;   // Nothing to write. A write touches the disk.

            stored.Artists[Key(artist)] = new BannerOrder(was?.Urls ?? [url], url, was?.Ranked ?? false);
            Write(stored);
        }
    }

    /// <summary>Artists are matched the way the rest of the app matches them. Case and surrounding spaces do
    /// not make two artists.</summary>
    private static string Key(string artist) => artist.Trim().ToLowerInvariant();

    /// <summary>The stored orders. The result is empty where there is no file. It is also empty where the file
    /// came from another version, because the orders are then worked out again. That is slow once, and then
    /// correct. It is also empty where the file does not parse. Such a file is moved aside.
    /// <c>MayWrite</c> is false when the file exists but could not be read. Nothing may be written then. See
    /// RPM.Core.Files.StoreFile.</summary>
    private static RPM.Core.Files.StoreRead<Stored> Read()
    {
        var read = RPM.Core.Files.StoreFile.Load(
            Path, text => JsonSerializer.Deserialize(text, BannerOrdersJson.Default.Stored));

        var stored = read.Value is { } found && found.Version == Version
            ? found with { Artists = found.Artists ?? [] }
            : new Stored(Version, []);

        return new RPM.Core.Files.StoreRead<Stored>(stored, read.MayWrite);
    }

    private static void Write(Stored stored)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path, JsonSerializer.Serialize(stored, BannerOrdersJson.Default.Stored));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An order that cannot be written costs a rank on the next launch, not a picture.
        }
    }
    /// <summary>The JSON serialiser for the stored orders.
    /// A published build is Native AOT, which serialises from this and never by reflection.</summary>
    [JsonSerializable(typeof(Stored))]
    private sealed partial class BannerOrdersJson : JsonSerializerContext;

}
