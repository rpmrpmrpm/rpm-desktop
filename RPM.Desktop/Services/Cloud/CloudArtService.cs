using System.Net.Http;
using RPM.Core.Cloud;

namespace RPM.Desktop.Services.Cloud;

/// <summary>
/// The sleeve of a record this machine does not hold.
///
/// <para>
/// A cover comes from the album folder's image, or from the tags of a file in that folder. A record that is
/// only in the cloud has neither. Two places can still answer. Both are asked here, in order of cost. The
/// first is the album cover the store keeps, one per album, which is what the phone draws. If that is
/// missing, the second is the art embedded in the track's own sidecar.
/// </para>
///
/// <para>
/// The sidecar fallback is needed. Album covers are stored when a track is uploaded, and they are repaired
/// by a pass that walks albums <em>with local files</em>. A library that has been fully offloaded has no
/// local files, so it is never walked, and it keeps no cover in the cloud. In that case every tile is
/// blank. The sidecar still has the picture that was in the file. So it is fetched, used, and stored as the
/// album's cover. That fixes the gap for every other device too.
/// </para>
///
/// <para>Which albums have a stored cover is read <b>once</b>, as an index. This stops a wall of tiles that
/// scrolls past forty records from making forty 404 requests. Pictures are kept on this disk under the
/// version the store stamped them with. A cover lives at an address derived from the album, so replacing it
/// does not move it. The stamp is the only way to tell a cached sleeve from a corrected one.</para>
/// </summary>
public sealed class CloudArtService
{
    private readonly RpmCloudClient _cloud;
    private readonly AuthService _auth;
    private readonly ILogger<CloudArtService> _log;
    private readonly HttpClient _http = RPM.Core.Net.Wire.Outside(RPM.Core.Net.Destination.Unaddressed, TimeSpan.FromSeconds(20));

    private readonly string _covers;

    /// <summary>Which albums have a cover, which have been asked about and have none, and where a fetched
    /// picture is kept. All of this follows <see cref="CloudArtIndex"/>'s rule. Guarded by
    /// <see cref="_gate"/>, because every tile on a scrolling wall asks at once.</summary>
    ///
    /// <remarks>There is one set of these notes for each storage that is asked: this account's own storage,
    /// under the empty key, and one for each person whose shelf is drawn. Two accounts key the same album
    /// in the same way, because the key is made from the artist and the album. So a single set of notes
    /// would let this account's "nothing there" answer stand for somebody else's record, and their cover
    /// would show for this account's record.</remarks>
    private readonly Dictionary<string, CloudArtIndex> _known = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>The store's listing for each owner, either in flight or already held. One call is shared by
    /// every tile.</summary>
    private readonly Dictionary<string, Task<IReadOnlyList<StoredCover>>> _index =
        new(StringComparer.Ordinal);

    public CloudArtService(RpmCloudClient cloud, AuthService auth, ILogger<CloudArtService> log)
    {
        _cloud = cloud;
        _auth = auth;
        _log = log;

        _covers = Path.Combine(LocalData.Dir, "cloud-art");
        try { Directory.CreateDirectory(_covers); } catch { /* the cover is decoration */ }

        _auth.Changed += () =>
        {
            if (_auth.IsLoggedIn) return;
            Forget();
        };
    }

    /// <summary>
    /// The cover for an album that is only in the cloud. Null when neither the store nor the track's own
    /// sidecar has one. That is ordinary, and for a tile it means the same thing: no picture.
    /// </summary>
    /// <param name="rpmId">Any track of the album. The sidecar fallback reads this. Without one, only the
    /// stored cover can answer.</param>
    /// <param name="owner">Whose storage to ask, when the record is not this account's. This is the account
    /// of somebody who has shared their collection. Their covers are the only ones there are for a record
    /// they have offloaded, because nobody else has the files it could be read from.</param>
    public async Task<byte[]?> CoverAsync(
        string? albumArtist, string? artist, string? album, string? rpmId,
        CancellationToken ct = default, string? owner = null)
    {
        if (!_auth.IsLoggedIn || string.IsNullOrWhiteSpace(album)) return null;

        // There are two spellings of the key. The store hashed the key from the file's own tags at upload
        // time. The catalogue this machine reads may not carry an album artist at all, because rows written
        // before the catalogue fields existed have none. Then the album artist and the track artist are two
        // different keys for one record. Asking under both costs two dictionary lookups.
        var keys = Keys(albumArtist, artist, album);
        var primary = keys[0];
        var whose = owner ?? "";

        lock (_gate)
            if (Known(whose).IsBlank(primary)) return null;

        try
        {
            if (await StoredAsync(keys, whose, ct) is { } stored)
            {
                var path = PathFor(whose, stored.AlbumKey, stored.UpdatedAt);
                if (File.Exists(path))
                    return await File.ReadAllBytesAsync(path, ct);

                if (await _cloud.GetAlbumArtUrlAsync(Shelf(whose), stored.AlbumKey, ct) is { Length: > 0 } url)
                {
                    var bytes = await _http.GetByteArrayAsync(url, ct);
                    if (bytes.Length > 0)
                    {
                        Write(path, bytes);
                        return bytes;
                    }
                }
            }

            // Nothing is stored for the album. The picture that was in the file is still in its sidecar,
            // but only in this account's own file. A sidecar in somebody else's storage cannot be read from
            // here, and their track id means nothing in this account's storage. So there is nothing to ask.
            if (owner is null && await FromSidecarAsync(primary, rpmId, ct) is { Length: > 0 } embedded)
                return embedded;

            lock (_gate) Known(whose).MarkBlank(primary);
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;   // The tile has scrolled away, so nobody is waiting for the answer.
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[Cloud] Could not fetch a stored cover for {Album}", album);
            return null;
        }
    }

    /// <summary>
    /// The disc label the store keeps for an album: the transparent picture of the record itself. Null when
    /// there is none, which is ordinary for most records.
    ///
    /// <para>There is no sidecar fallback, unlike the cover. A disc label is never in a file's tags, so the
    /// store is the only place it can have come from.</para>
    /// </summary>
    public async Task<byte[]?> DiscAsync(
        string? albumArtist, string? artist, string? album, CancellationToken ct = default)
    {
        if (!_auth.IsLoggedIn || string.IsNullOrWhiteSpace(album)) return null;

        var key = AlbumArt.DiscKeyForTrack(albumArtist, artist, album);

        lock (_gate)
            if (Known("").IsBlank(key)) return null;

        try
        {
            if (await StoredAsync([key], "", ct) is { } stored)
            {
                var path = PathFor("", stored.AlbumKey, stored.UpdatedAt);
                if (File.Exists(path))
                    return await File.ReadAllBytesAsync(path, ct);

                if (await _cloud.GetAlbumArtUrlAsync(CloudShelf.Own, stored.AlbumKey, ct) is { Length: > 0 } url)
                {
                    var bytes = await _http.GetByteArrayAsync(url, ct);
                    if (bytes.Length > 0)
                    {
                        Write(path, bytes);
                        return bytes;
                    }
                }
            }

            lock (_gate) Known("").MarkBlank(key);
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[Cloud] Could not fetch a stored disc label for {Album}", album);
            return null;
        }
    }

    /// <summary>
    /// Sends this machine's picture of a record to every other device. The cover is stored under the
    /// album's key, and the disc label under its own key.
    ///
    /// <para>It is sent as soon as the editor writes it. The sync run's repair pass is not used for this,
    /// because the person looking at the library on a phone expects to see the sleeve they have just
    /// chosen. The index this service holds is dropped afterwards. That way nothing here, and nothing on
    /// the next wall rebuild, keeps showing the picture that has just been replaced.</para>
    /// </summary>
    /// <returns>Whether anything was sent. False when there is nothing to send, when nobody is signed in,
    /// or when the upload failed. The picture is on this disk either way.</returns>
    public async Task<bool> PublishAsync(
        string? albumArtist, string? artist, string? album,
        AlbumCover? cover, AlbumCover? disc, CancellationToken ct = default)
    {
        if (!_auth.IsLoggedIn || string.IsNullOrWhiteSpace(album)) return false;
        if (cover is null && disc is null) return false;

        var sent = false;
        try
        {
            if (cover is not null)
                sent |= await _cloud.UploadAlbumArtAsync(
                    AlbumArt.KeyForTrack(albumArtist, artist, album), cover, ct);

            if (disc is not null)
                sent |= await _cloud.UploadAlbumArtAsync(
                    AlbumArt.DiscKeyForTrack(albumArtist, artist, album), disc, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "[Cloud] Could not send the artwork for {Album}", album);
        }

        if (sent)
        {
            _log.LogInformation("[Cloud] Sent the artwork for {Album}: cover={Cover} disc={Disc}",
                album, cover is not null, disc is not null);
            Invalidate(albumArtist, artist, album);
        }
        return sent;
    }

    /// <summary>
    /// Removes a record's pictures from the cloud, for somebody who has removed them on this machine.
    ///
    /// <para>The removal must be sent to the cloud, or it does not hold. A cover lives at an address
    /// derived from the album. A machine that still has a cover, and sees that another machine has none,
    /// reads the gap as something to fill. The deleted picture would then come straight back on the next
    /// sync run.</para>
    /// </summary>
    /// <param name="cover">Whether the sleeve was removed.</param>
    /// <param name="disc">Whether the disc label was removed.</param>
    public async Task RemoveAsync(
        string? albumArtist, string? artist, string? album, bool cover, bool disc,
        CancellationToken ct = default)
    {
        if (!_auth.IsLoggedIn || string.IsNullOrWhiteSpace(album)) return;
        if (!cover && !disc) return;

        try
        {
            // The album's key is checked in both spellings where they differ. The picture may have been
            // stored by a machine that keyed it under the track artist. A copy left under the other key
            // would come back.
            if (cover)
                foreach (var key in Keys(albumArtist, artist, album))
                    await _cloud.DeleteAlbumArtAsync(key, ct);

            if (disc)
                await _cloud.DeleteAlbumArtAsync(AlbumArt.DiscKeyForTrack(albumArtist, artist, album), ct);

            _log.LogInformation("[Cloud] Took the artwork of {Album} away: cover={Cover} disc={Disc}",
                album, cover, disc);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "[Cloud] Could not remove the artwork for {Album}", album);
        }

        Invalidate(albumArtist, artist, album);
    }

    /// <summary>
    /// Forgets what this service knows about one album's pictures. This clears the "nothing there" note and
    /// the stored index, so the next request goes to the store.
    ///
    /// <para>This is called whenever a record's art changes on this machine. A cover lives at an address
    /// derived from the album, so replacing it moves nothing. Without this call, an album that had no art a
    /// minute ago would still be remembered as blank. An album that has a new picture would keep handing
    /// out the old stamp.</para>
    /// </summary>
    public void Invalidate(string? albumArtist, string? artist, string? album)
    {
        if (string.IsNullOrWhiteSpace(album)) return;

        lock (_gate)
        {
            // Every storage is cleared, not only this account's. The key is made from the artist and the
            // album, so a record whose art changed here is the same record on a friend's shelf.
            string[] changed =
                [.. Keys(albumArtist, artist, album), AlbumArt.DiscKeyForTrack(albumArtist, artist, album)];

            foreach (var known in _known.Values) known.Invalidate(changed);
            _index.Clear();
        }
    }

    /// <summary>What is known about one storage's covers. It is made on first use, under
    /// <see cref="_gate"/>.</summary>
    private CloudArtIndex Known(string owner)
    {
        if (!_known.TryGetValue(owner, out var known))
            _known[owner] = known = new CloudArtIndex();
        return known;
    }

    private static string[] Keys(string? albumArtist, string? artist, string? album) =>
        CloudArtIndex.KeysFor(albumArtist, artist, album);

    /// <summary>Whose covers a read is for, from the key this cache files them under. Empty means this
    /// account's own. This is what every path uses, except a borrowed shelf.
    ///
    /// <para>Absent (null) means the same as empty. The raw owner parameter can be null, and a null here
    /// throws. So callers must pass the normalised value, not the raw parameter.</para></summary>
    private static CloudShelf Shelf(string? owner) =>
        owner is not { Length: > 0 } ? CloudShelf.Own : CloudShelf.Of(owner);

    /// <summary>The store's entry for whichever of the keys it knows, or null when it knows none.</summary>
    private async Task<StoredCover?> StoredAsync(
        IReadOnlyList<string> keys, string owner, CancellationToken ct)
    {
        Task<IReadOnlyList<StoredCover>> index;
        lock (_gate)
        {
            var known = Known(owner);
            if (!_index.TryGetValue(owner, out var held) || known.NeedsIndex(DateTime.UtcNow))
            {
                known.NoteIndexRead(DateTime.UtcNow);
                _index[owner] = held = ReadIndexAsync(owner);
            }
            index = held;
        }

        // A listing that cannot be read throws here. CoverAsync and DiscAsync then return without marking
        // anything blank, and without reading a sidecar. "Not stored" is only ever concluded from a listing
        // that was read.
        return CloudArtIndex.Match(await index.WaitAsync(ct), keys);
    }

    /// <summary>One read of the store's listing, shared by every tile that asks while it is held. A failure
    /// is noted once, here. The next read is due after the short hold, not after the listing's full life.
    /// The failure is thrown to every waiter.</summary>
    private async Task<IReadOnlyList<StoredCover>> ReadIndexAsync(string owner)
    {
        try
        {
            return await _cloud.GetAlbumArtCatalogAsync(Shelf(owner), CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (_gate) Known(owner).NoteIndexFailed(DateTime.UtcNow);
            throw;
        }
    }

    /// <summary>
    /// The art embedded in the track's own file, taken from the sidecar that the cloud keeps beside it.
    /// Once it is in hand, it is stored as the album's cover, so nothing has to do this again.
    ///
    /// <para>A sidecar holds the whole tag set of the file, with the picture base64-encoded inside it. So
    /// it is megabytes, and it is asked for exactly once per album. The answer is written to this disk
    /// either way. An album with nothing in its sidecar is remembered as blank.</para>
    /// </summary>
    private async Task<byte[]?> FromSidecarAsync(string albumKey, string? rpmId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rpmId)) return null;

        // The version is zero. This picture came from a file, not from the store, so there is no stamp to
        // key it on. It is replaced as soon as the store has one of its own.
        var path = PathFor("", albumKey, CloudArtIndex.FromSidecar);
        if (File.Exists(path))
            return await File.ReadAllBytesAsync(path, ct);

        var sidecar = await _cloud.GetSidecarAsync(rpmId, ct);
        if (sidecar?.Art is not { Length: > 0 } art) return null;

        Write(path, art);

        // This covers a real gap. An album offloaded from every machine has nobody left who can upload its
        // sleeve. So the picture is stored where the store keeps covers. This is best effort: the tile
        // already has its picture, whatever happens here.
        try
        {
            var mime = string.IsNullOrWhiteSpace(sidecar.ArtMime) ? "image/jpeg" : sidecar.ArtMime!;
            if (await _cloud.UploadAlbumArtAsync(albumKey, new AlbumCover(art, mime, AlbumCover.SourceEmbedded), ct))
            {
                _log.LogInformation(
                    "[Cloud] Stored the sleeve of a cloud-only album from its sidecar ({Key})", albumKey);

                lock (_gate) { _index.Remove(""); Known("").Invalidate([albumKey]); }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "[Cloud] Could not store the recovered sleeve for {Key}", albumKey);
        }

        return art;
    }

    private void Write(string path, byte[] bytes)
    {
        var partial = path + ".part";
        try
        {
            // The picture is written to a temporary name beside the real one, then moved into place. A
            // half-written picture that looked complete would decode as a torn tile for as long as it
            // stayed cached.
            File.WriteAllBytes(partial, bytes);
            File.Move(partial, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(partial); } catch { /* nothing to do about it */ }
        }
    }

    private void Forget()
    {
        lock (_gate)
        {
            _known.Clear();
            _index.Clear();
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(_covers))
                try { File.Delete(file); } catch { /* in use, or already gone */ }
        }
        catch
        {
            // Everything in this folder can be fetched again, so losing it is harmless.
        }
    }

    /// <summary>Where a picture is kept on this disk. For a borrowed shelf, the owner is part of the file
    /// name. Two accounts key the same album in the same way, and their sleeve is not this account's
    /// sleeve.</summary>
    private string PathFor(string owner, string albumKey, long version)
        => Path.Combine(_covers, CloudArtIndex.CacheFileName(
            owner.Length == 0 ? albumKey : $"{owner}-{albumKey}", version));
}
