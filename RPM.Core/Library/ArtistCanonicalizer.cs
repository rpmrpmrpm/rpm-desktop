using RPM.Core.Models;
using RPM.Fingerprint.Catalog;

namespace RPM.Core.Library;

/// <summary>Makes an incoming track spell an artist the way the collection already spells them.
///
/// <para>This solves the other half of the messy-library problem. File permissions do not help with this
/// half. Files arrive tagged by whoever ripped them: "Guns n Roses", "GUNS N' ROSES", "guns n' roses", "The
/// Guns N' Roses". A naming template treats each one as a different folder, so one band becomes four
/// shelves. Once there are four shelves, nothing later can tell that they were one band. The fingerprint
/// layer answers "what recording is this". This class answers "what does this collection call the person
/// who made it".</para>
///
/// <para>Equivalence is <see cref="MetadataCleaner.ComparisonKey"/>. It is the same key the server uses to
/// decide whether two fingerprint reports make the same claim. So a name only ever resolves to a spelling
/// the library already uses. It never invents a spelling, and it never merges two names that the library
/// keeps apart. A name with no matching key is returned exactly as it arrived.</para>
/// </summary>
public sealed class ArtistCanonicalizer
{
    /// <summary>The comparison key mapped to the spelling the collection has settled on.</summary>
    private readonly Dictionary<string, string> _byKey = new(StringComparer.Ordinal);

    /// <summary>The index for an empty library. Nothing is established, so every name comes back as it
    /// arrived.
    /// </summary>
    public static ArtistCanonicalizer Empty { get; } = new();

    private ArtistCanonicalizer() { }

    /// <summary>Builds the index from the artists the library already holds. The most-used spelling of
    /// each artist wins. This is the only rule that makes sense: that spelling is on the most shelves, so
    /// adopting it moves the fewest files and matches what the user already sees.
    /// </summary>
    public static ArtistCanonicalizer FromLibrary(LibraryDatabase db) =>
        From(db.ArtistNames().Select(a => a.Name));

    /// <summary>The index over a given set of established names, most established first. An earlier name
    /// wins its key.</summary>
    public static ArtistCanonicalizer From(IEnumerable<string> established)
    {
        var canon = new ArtistCanonicalizer();
        foreach (var name in established)
            canon.Learn(name);
        return canon;
    }

    /// <summary>Records a spelling as established, unless its key already is. Import calls this as it
    /// goes, so a batch agrees with itself. A folder with files split between "Sigur Ros" and "Sigur Rós"
    /// gets one name, even if the library had never seen the band before this import.
    /// </summary>
    public void Learn(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;
        var key = MetadataCleaner.ComparisonKey(name);
        // A name that cleans to nothing (punctuation, an underscore, a stray bracket) would take the empty
        // key. Every other unnamed track would then get that name back. So it is skipped.
        if (key.Length == 0)
            return;
        _byKey.TryAdd(key, name.Trim());
    }

    /// <summary>The collection's spelling of this artist, or the name unchanged when the collection has no
    /// spelling for it. Null and blank come back as they are. A missing artist is a job for identification,
    /// not for spelling.
    /// </summary>
    public string? Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return name;
        var key = MetadataCleaner.ComparisonKey(name);
        return key.Length > 0 && _byKey.TryGetValue(key, out var established) ? established : name;
    }

    /// <summary>The track with its artist and album artist spelled the collection's way. Both names are
    /// then learned, so the first file of an unknown band sets the spelling for the rest of the run.
    /// Nothing else on the track is changed. This decides how a name is written, never what the name is.
    /// </summary>
    public TrackInfo Apply(TrackInfo track)
    {
        var artist = Resolve(track.Artist);
        var albumArtist = Resolve(track.AlbumArtist);

        Learn(artist);
        Learn(albumArtist);

        return artist == track.Artist && albumArtist == track.AlbumArtist
            ? track
            : track with { Artist = artist, AlbumArtist = albumArtist };
    }
}
