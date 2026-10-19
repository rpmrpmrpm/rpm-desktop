namespace RPM.Core.Cloud;

/// <summary>
/// What this machine knows about the covers the store keeps: which albums have one, which have been
/// asked about and have none, and where a fetched picture is kept on this disk.
///
/// <para>
/// This is the bookkeeping, not the fetching. A wall that scrolls past forty albums asks forty times,
/// so wrong answers here are expensive: reading the index for every tile means forty listings, a
/// missing negative answer means forty 404s per scroll, and a cache address that does not change when
/// a cover is replaced shows the old cover for ever.
/// </para>
/// </summary>
public sealed class CloudArtIndex
{
    /// <summary>Albums that nothing could be found for, so a wall of them does not make a request per
    /// tile per scroll.</summary>
    private readonly HashSet<string> _blank = new(StringComparer.OrdinalIgnoreCase);

    private DateTime _indexRead = DateTime.MinValue;

    /// <summary>How long the store's listing is trusted. A cover replaced on another machine shows up
    /// within this time. Asking more often would mean a listing for every wall.</summary>
    public static readonly TimeSpan IndexLife = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The version under which a picture recovered from a track's sidecar is filed.
    ///
    /// <para>Zero, because the picture came from a file and not from the store, so there is no stamp to
    /// key it on. It is replaced as soon as the store has a cover of its own, because any real stamp
    /// sorts above zero.</para>
    /// </summary>
    public const long FromSidecar = 0;

    /// <summary>
    /// Both forms of an album's key.
    ///
    /// <para>The store hashed its key from the file's own tags at upload time. The catalogue that this
    /// machine reads may have no album artist at all (rows written before the catalogue had that
    /// field), and then the album artist and the track artist give two different keys for one album.
    /// Asking with both costs two dictionary lookups.</para>
    /// </summary>
    public static string[] KeysFor(string? albumArtist, string? artist, string? album)
    {
        var byAlbumArtist = AlbumArt.KeyForTrack(albumArtist, artist, album);
        var byArtist = AlbumArt.KeyFor(artist, album);

        return string.Equals(byAlbumArtist, byArtist, StringComparison.Ordinal)
            ? [byAlbumArtist]
            : [byAlbumArtist, byArtist];
    }

    /// <summary>
    /// Where a picture is kept on this disk, from the album's key and the version the store stamped it
    /// with.
    ///
    /// <para>A cover lives at an address derived from the album, so replacing it does not move it. The
    /// stamp is the only thing that tells a cached cover from a corrected one. The key is a hash, but
    /// the file name is built from a value that came from the network, so it is reduced to letters and
    /// digits either way.</para>
    /// </summary>
    public static string CacheFileName(string albumKey, long version) =>
        $"{string.Concat(albumKey.Where(char.IsLetterOrDigit))}-{version}.img";

    /// <summary>The store's entry for whichever of the keys it knows, or null when it knows none.</summary>
    public static StoredCover? Match(IReadOnlyList<StoredCover> covers, IReadOnlyList<string> keys)
    {
        ArgumentNullException.ThrowIfNull(covers);
        ArgumentNullException.ThrowIfNull(keys);

        if (covers.Count == 0) return null;

        var byKey = new Dictionary<string, StoredCover>(covers.Count, StringComparer.OrdinalIgnoreCase);

        // An indexer and not ToDictionary: a duplicate key in the listing is not worth an exception in the
        // middle of showing covers. The last one wins, and the listing is stable, so an album that the
        // account holds twice always resolves to the same entry.
        foreach (var cover in covers) byKey[cover.AlbumKey] = cover;

        foreach (var key in keys)
            if (byKey.TryGetValue(key, out var found))
                return found;

        return null;
    }

    /// <summary>Whether the store's listing has to be read again.</summary>
    public bool NeedsIndex(DateTime nowUtc) => nowUtc - _indexRead > IndexLife;

    /// <summary>The listing was just requested. Clears the "nothing there" notes: a cover stored since
    /// the last read is no longer nothing.</summary>
    public void NoteIndexRead(DateTime nowUtc)
    {
        _indexRead = nowUtc;
        _blank.Clear();
    }

    /// <summary>How long to wait before asking again after a listing could not be read. Short, because
    /// a failure says nothing about the covers. Not zero, because a wall asks once per tile and the
    /// failed task is shared until then.</summary>
    public static readonly TimeSpan FailedIndexHold = TimeSpan.FromSeconds(30);

    /// <summary>The listing could not be read. The next read is due after <see cref="FailedIndexHold"/>
    /// and not after <see cref="IndexLife"/>. Nothing is marked blank: if a failed read counted as an
    /// empty listing, every album would be "nothing stored", and covers found in sidecars would be
    /// uploaded over the ones the store really has.</summary>
    public void NoteIndexFailed(DateTime nowUtc)
        => _indexRead = nowUtc - IndexLife + FailedIndexHold;

    /// <summary>Whether this album has already been asked about and has nothing.</summary>
    public bool IsBlank(string key) => _blank.Contains(key);

    public void MarkBlank(string key) => _blank.Add(key);

    /// <summary>
    /// Forgets what is known about one album's pictures: both the "nothing there" note and the held
    /// listing.
    ///
    /// <para>Called wherever an album's art changes on this machine. A cover lives at an address derived
    /// from the album, so replacing it moves nothing. Without this call, an album that had no art a
    /// minute ago stays remembered as blank, and an album with a different picture keeps handing out
    /// the old stamp.</para>
    /// </summary>
    public void Invalidate(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        foreach (var key in keys) _blank.Remove(key);
        _indexRead = DateTime.MinValue;
    }

    /// <summary>Everything is forgotten, on signing out and on signing in as somebody else.</summary>
    public void Clear()
    {
        _blank.Clear();
        _indexRead = DateTime.MinValue;
    }
}
