using RPM.Core.Net;

namespace RPM.Desktop.Services.Platform;

/// <summary>
/// The one folder the app keeps data from other services in, and the one switch that empties it.
///
/// <para>Everything under here was fetched from a service and can be fetched again: artwork, the station
/// directory, the catalogues behind the track pane, and the words of a song. None of it is the user's own.
/// Their music, library, sign-in and cloud data are kept beside this folder, and clearing it never touches
/// them. A person who wants to free space, or who thinks the app shows something out of date, can clear the
/// cache with one action and lose none of their own data.</para>
///
/// <para>Folders from earlier versions are moved or removed once, by <see cref="Retire"/>.</para>
/// </summary>
public static class AppCache
{
    private static readonly Lazy<bool> Retired = new(Retire);

    private static readonly Lazy<WebCacheStore> Lazy = new(() => new WebCacheStore(Dir));

    /// <summary>The cache folder, inside the app's own data folder.</summary>
    public static string Dir => Path.Combine(LocalData.Dir, "cache");

    /// <summary>Where lyrics are kept, as one file per record. A sheet is a document that a person may want
    /// to open, so it is a file and not a database row. It is under this folder, so clearing the cache
    /// also removes the lyrics.</summary>
    public static string LyricsDir
    {
        get
        {
            _ = Retired.Value;   // The first use of the cache runs Retire once, before anything is read.
            return Path.Combine(Dir, "lyrics");
        }
    }

    /// <summary>The store every cached client in the app writes to.</summary>
    public static WebCacheStore Store
    {
        get
        {
            _ = Retired.Value;
            return Lazy.Value;
        }
    }

    /// <summary>A client that answers from the cache before it asks the network.</summary>
    /// <param name="category">Which cache its answers belong to: "art", "radio", "facts".</param>
    /// <param name="where">Where it points. Stated rather than defaulted, and aimed here rather than by
    /// the caller afterwards: a client built and then aimed is a client somebody can forget to aim, and the app key
    /// rides on the answer.</param>
    /// <param name="timeout">Applies to a request that actually goes out; a cached answer is instant.</param>
    public static HttpClient Client(string category, Destination where, TimeSpan? timeout = null)
        => Client(WebCachePolicy.For(category), where, timeout);

    /// <summary>The same client, with answers filed under a policy the caller built: for a category
    /// whose answers need a namespace of their own (see <see cref="WebCachePolicy.Vary"/>).</summary>
    public static HttpClient Client(WebCachePolicy policy, Destination where, TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(20);

        // The same limit is set twice on purpose. The client's timeout only applies to requests that the
        // client cancels itself. A request this cache shares between callers is not cancelled by any one
        // caller, so the handler is also told how long it may keep a shared request open. The network gate
        // sits under the cache, not over it. An answer already on disk is served even when the machine has
        // no outside connection. Only a request that would have to leave the machine is refused.
        var client = new HttpClient(new HttpCacheHandler(
            Store, policy, inner: Wire.GatedSockets(), flightTimeout: limit))
        {
            Timeout = limit,
        };
        where.Aim(client);
        return client;
    }

    /// <summary>What the cache is taking up, for the line under the button.</summary>
    public static long BytesOnDisk()
    {
        var total = Store.BytesOnDisk();
        try
        {
            if (Directory.Exists(LyricsDir))
            {
                foreach (var file in Directory.EnumerateFiles(LyricsDir, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(file).Length; }
                    catch (Exception ex) when (ex is not OperationCanceledException) { }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }
        return total;
    }

    /// <summary>Empties it. Everything in here can be fetched again, so this cannot lose anything but
    /// time.</summary>
    public static void Clear()
    {
        Store.Clear();

        try
        {
            if (Directory.Exists(LyricsDir))
                Directory.Delete(LyricsDir, recursive: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Moves or removes the folders from earlier versions. This runs once, on the first use of the cache.
    ///
    /// <para>Artwork rows are deleted, not carried over. They were keyed by a hash of artist and album, and
    /// they held the app's reading of an answer. The store holds the answer itself, so there is nothing to
    /// convert. The cost is one lookup per artist browsed, and nothing else. The lyrics are moved, because
    /// a sheet is expensive to fetch again and its file format has not changed.</para>
    /// </summary>
    private static bool Retire()
    {
        Directory.CreateDirectory(Dir);

        try
        {
            var lyrics = Path.Combine(LocalData.Dir, "lyrics");
            var moved = Path.Combine(Dir, "lyrics");
            if (Directory.Exists(lyrics) && !Directory.Exists(moved))
                Directory.Move(lyrics, moved);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }

        try
        {
            var artwork = Path.Combine(LocalData.Dir, "artwork");
            if (Directory.Exists(artwork))
                Directory.Delete(artwork, recursive: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }

        return true;
    }
}
