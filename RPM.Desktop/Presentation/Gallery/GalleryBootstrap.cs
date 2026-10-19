#if DEBUG
using RPM.Core.Cloud;
using RPM.Core.Library;
using RPM.Core.Metadata.Providers;
using RPM.Desktop.Services.Account;

namespace RPM.Desktop.Presentation.Gallery;

/// <summary> Sets up a <c>--gallery</c> run. It lays down a throwaway library under the temp folder and
/// seeds it with the demo catalogue (<see cref="DemoLibrary"/>). It then switches <see
/// cref="GalleryMode"/> on, so the rest of the app runs against it. The shell opens this library on
/// launch instead of the user's own, and the library sync stays off for the run. </summary>
internal static class GalleryBootstrap
{
    /// <summary> How long the one real file runs. It is short on purpose. The greeting it carries ends
    /// when the record is over, and that ending is part of what there is to see. A full four minutes
    /// would be too long for anyone to wait for, so nobody would see the ending. </summary>
    private const int RecordSeconds = 30;

    /// <param name="harness">The run's session home, which is what makes the run signed out. See
    /// <see cref="GalleryMode.Begin"/>.</param>
    public static void Prepare(SessionHome.Harness harness)
    {
        var root = Path.Combine(Path.GetTempPath(), $"rpm-gallery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        // The database is written to disk here and reopened by the shell's LibraryService. The seed
        // survives the handoff because it is committed to the file, not held in memory.
        using (var db = new LibraryDatabase(MirrorLocation.InFolder(root)))
        {
            db.UpsertMany(WithStreamFacts(WithTheOneRealRecord(root, DemoLibrary.Tracks())));
            Dress(root, db);
        }

        GalleryMode.Begin(harness, root);
    }

    /// <summary> The same harness, opened on the thousand-album wall library instead of the seven-album
    /// demo. This makes it possible to measure a launch at a size that matters.
    ///
    /// <para><see cref="GalleryWallLibrary"/> is the right library for this. It cannot be the library
    /// the app opens on by itself, because the gallery index reaches it after the window is up, and
    /// startup is over by then. A startup cost that grows with the collection cannot be measured from a
    /// launch that opens seven records.</para>
    ///
    /// <para>The first run draws a thousand covers and takes a minute. Later runs reuse them. The
    /// library is generated into a stable temp folder, and the loop skips any cover already on
    /// disk.</para> </summary>
    public static void PrepareWall(SessionHome.Harness harness)
    {
        GalleryMode.Begin(harness, GalleryWallLibrary.Ensure());
    }

    /// <summary> The harness opened on a shelf of REAL sleeves. There is one album per picture in the
    /// folder named on the command line. An invented catalogue is written to fit them (see <see
    /// cref="GalleryShelfLibrary"/>).
    ///
    /// <para>This is for photographing the interface. The other two libraries draw their own covers.
    /// That suits driving a screen, but it is wrong for taking a picture of one. A wall of generated
    /// shapes looks like a mock-up from across the room. The shelf is the thing worth showing.</para>
    /// </summary>
    /// <param name="artistsDir">A folder of artist portraits, or null for a shelf whose artists have
    /// never been photographed. There is no default. Which of the two pictures is being taken is for
    /// the caller to say, and both are pictures somebody wants.</param>
    public static void PrepareShelf(SessionHome.Harness harness, string coversDir, string? artistsDir)
    {
        GalleryMode.Begin(harness, GalleryShelfLibrary.Ensure(coversDir, artistsDir));

        // This runs after the library is made, because the artists are filed under the record that the
        // library was just given. It also reaches for services on the host, which is why it is not part
        // of laying the shelf down.
        GalleryShelfLibrary.Papers();
    }

    /// <summary> Everything a record on the demo shelf needs in order to look lived in. It has a sleeve
    /// on disk, a pressing it was identified as, stars given by the listener, songs the listener loved,
    /// and half of it in the cloud.
    ///
    /// <para>These are seeded into the library rather than answered by a stand-in. Each one is
    /// something the app reads out of its own mirror. A harness that faked them at the view would be
    /// showing a picture of the album page. This seeds the real data, so it shows the album page
    /// itself.</para> </summary>
    private static void Dress(string root, LibraryDatabase db)
    {
        var folders = DemoLibrary.Tracks()
            .Select(t => (t.Folder, t.AlbumArtist, t.Album))
            .Distinct()
            .ToList();

        // The listener's own marks, spread so the shelf is not uniform. Some records are favourites,
        // some are rated, and one has been passed over entirely. That is what most of a real shelf
        // looks like, and it is the state the page must still draw well.
        var stars = new[] { 5, 4, 0, 5, 3, 4, 5 };
        var loved = new[] { true, false, false, true, false, false, true };

        for (var i = 0; i < folders.Count; i++)
        {
            var (folder, artist, album) = folders[i];

            // A real sleeve in a real folder. The page reads cover.jpg from disk, as it would for an
            // imported album.
            var dir = Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "cover.jpg"), GalleryArt.CoverJpeg(artist, album));

            var pressing = GalleryCommunity.Pressing(artist ?? "", album ?? "");
            db.SetAlbumPressing(folder, DiscogsJson.ProviderId, pressing.ReleaseId);
            db.SetAlbumRelease(folder, pressing.Catno, pressing.Released);

            if (loved[i % loved.Length])
                db.SetAlbumFavorite(folder, true);
            if (stars[i % stars.Length] > 0)
                db.SetAlbumReview(folder, stars[i % stars.Length], null, DateTime.UtcNow.Ticks);
        }

        // Songs the listener loved, and a record half in the cloud. Both are per-track marks. The
        // tracklist draws a column for each of them, and neither one shows on an untouched library.
        var now = DateTime.UtcNow.Ticks;
        foreach (var track in db.Query())
        {
            if (track.TrackNumber % 4 == 3)
                db.SetTrackFavorite(track.RelativePath, true, now);

            // Most of the shelf is in the cloud. A couple of tracks per album are not, and one is on
            // its way. That is the only state that makes the badge pulse. Nothing is sent: the harness
            // runs signed out, and every upload path asks for an account first.
            db.SetCloudState(track.Id,
                track.TrackNumber % 5 == 0 ? CloudState.Local
                : track.TrackNumber % 7 == 0 ? CloudState.Queued
                : CloudState.Synced);
        }
    }

    /// <summary> Gives the demo rows the values a stream would have reported: the codec, the bit depth
    /// and rate, and the channel count. The album page prints all three under the title. A row that has
    /// never been read prints a record with no format at all. </summary>
    private static IReadOnlyList<LibraryTrack> WithStreamFacts(IReadOnlyList<LibraryTrack> rows)
        => [.. rows.Select(row => row.Extension switch
        {
            ".flac" => row with { Channels = 2, SampleRate = 44100, BitsPerSample = 16 },
            ".wav" => row with { Channels = 2, SampleRate = 44100, BitsPerSample = 16 },
            _ => row with { Channels = 2, SampleRate = 44100, BitrateKbps = 320 },
        })];

    /// <summary> Puts a real, playable file behind the one demo row that has to be played, not only
    /// listed. The row is then made to match the file.
    ///
    /// <para>Every other row in the catalogue names a file that is not there. That is fine for a screen
    /// that only lists rows. It is fatal for the one feature that starts a record playing. A track that
    /// cannot be opened is skipped to the next one. That is the answer <see
    /// cref="RPaMp.Models.RPaMpModel"/> gives for an offloaded file, and it is what the player does. If
    /// three tracks fail in a row, the room re-dresses itself from a different sleeve each time, and
    /// the greeting shows a flicker before the listener's own composition arrives.</para>
    ///
    /// <para>The row is found by the greeting's own rule, not by name. So this follows the catalogue if
    /// the record is moved or retitled. It does nothing if the record is taken out, which is the same
    /// result a real library without the record gets.</para> </summary>
    private static IReadOnlyList<LibraryTrack> WithTheOneRealRecord(
        string root, IReadOnlyList<LibraryTrack> rows)
    {
        var seeded = new List<LibraryTrack>(rows.Count);
        foreach (var row in rows)
        {
            if (!NewYear.IsTheRecord(row.Title, row.Artist))
            {
                seeded.Add(row);
                continue;
            }

            var path = Path.Combine(root, row.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // The shoot's clip, shortened. It is written for a spectrum, not for an ear, so every band
            // of the run has a voice in it. A plain tone would not do that. A stage that measures each
            // third of the range against its own recent peak will raise an empty third until it draws
            // the noise floor. A record with nothing in the middle would then show noise on the screen.
            var wav = GalleryShoot.Clip(RecordSeconds);
            File.WriteAllBytes(path, wav);

            // The row describes the file, not an invented one. The duration is what the
            // deck's clock and the position bar count against. The size is what a sync would compare.
            // Neither runs in a gallery, but a row that disagrees with the file under it would mislead
            // whoever reads this code next.
            seeded.Add(row with { DurationSeconds = RecordSeconds, SizeBytes = wav.LongLength });
        }

        return seeded;
    }
}
#endif
