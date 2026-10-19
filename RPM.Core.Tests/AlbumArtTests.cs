using RPM.Core.Cloud;

namespace RPM.Core.Tests;

/// <summary>
/// The album key is a contract between two programs that never speak to each other: the desktop
/// hashes it to decide where to PUT a cover, the phone hashes it to decide where to ask for one, and
/// nothing carries it between them. If they ever disagree, no cover is ever found and nothing
/// reports an error: the wall stays empty.
/// </summary>
public class AlbumArtTests
{
    [Fact]
    public void The_same_album_hashes_the_same_however_it_is_written()
    {
        // Case and surrounding whitespace are the two ways the same album arrives differently from a
        // tag reader and from a grouped listing.
        var canonical = AlbumArt.KeyFor("Manu Chao", "Clandestino");

        Assert.Equal(canonical, AlbumArt.KeyFor("manu chao", "clandestino"));
        Assert.Equal(canonical, AlbumArt.KeyFor("  Manu Chao  ", "Clandestino "));
        Assert.Equal(canonical, AlbumArt.KeyFor("MANU CHAO", "CLANDESTINO"));
    }

    [Fact]
    public void Different_albums_do_not_collide()
    {
        Assert.NotEqual(AlbumArt.KeyFor("Manu Chao", "Clandestino"),
                        AlbumArt.KeyFor("Manu Chao", "Próxima Estación"));

        // The same album title by two artists is two albums. This is the case the phone's own
        // client-side grouping still gets wrong, and the key must not repeat the mistake.
        Assert.NotEqual(AlbumArt.KeyFor("Weezer", "Greatest Hits"),
                        AlbumArt.KeyFor("Queen", "Greatest Hits"));
    }

    [Fact]
    public void The_key_is_safe_as_a_path_component_whatever_the_album_is_called()
    {
        // It ends up in an S3 key under the user's own prefix, so an album called "../../etc" must not
        // be able to reach out of it. Hashing is what guarantees that, rather than escaping.
        string[] nasty =
        [
            "../../../etc/passwd",
            "AC/DC",
            "Album: The \"Deluxe\" Edition",
            "\u202Ereversed",
            "🎵 emoji 🎵",
            "",
        ];

        foreach (var name in nasty)
        {
            var key = AlbumArt.KeyFor("Artist", name);

            Assert.Equal(32, key.Length);
            Assert.All(key, c => Assert.True(c is >= '0' and <= '9' or >= 'a' and <= 'f',
                $"'{c}' is not lower-case hex, from album \"{name}\""));
        }
    }

    [Fact]
    public void A_missing_artist_or_album_still_produces_a_key()
    {
        // "Unknown" albums are real and they still want one cover between them rather than a crash.
        Assert.Equal(32, AlbumArt.KeyFor(null, null).Length);
        Assert.Equal(AlbumArt.KeyFor(null, null), AlbumArt.KeyFor("", "   "));
    }

    [Fact]
    public void The_hash_is_pinned()
    {
        // Changing the derivation orphans every cover already stored, for every user, silently. If
        // this test fails, that is what is about to happen. Do not update it without a migration.
        Assert.Equal("d90d382db82e7ba38c335b59ae3eaf33", AlbumArt.KeyFor("Manu Chao", "Clandestino"));
    }

    // ── the content type a cover is stored under ──

    /// <summary>
    /// The type is part of the presigned signature, and the server signs only for these three. What a
    /// caller sends must therefore already be one of them, or the PUT header will not match what was
    /// signed and S3 refuses the upload.
    /// </summary>
    [Theory]
    [InlineData("image/png", "image/png")]
    [InlineData("image/webp", "image/webp")]
    [InlineData("image/jpeg", "image/jpeg")]
    public void An_accepted_type_is_left_alone(string mime, string expected)
        => Assert.Equal(expected, AlbumArt.NormalizeMime(mime));

    /// <summary>
    /// "image/jpg" is what the ID3 APIC frame says in many mp3 files. The server signs the slot for
    /// "image/jpeg", so if "image/jpg" were sent as it is, S3 would reject the upload with a 403 and
    /// the album would reach the cloud with no cover.
    /// </summary>
    [Theory]
    [InlineData("image/jpg")]
    [InlineData("IMAGE/JPG")]
    [InlineData("  image/jpeg  ")]
    [InlineData("image/bmp")]
    [InlineData("jpeg")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_becomes_the_type_the_server_would_have_signed_for(string? mime)
        => Assert.Equal("image/jpeg", AlbumArt.NormalizeMime(mime));

    /// <summary>Normalising twice gives the same result as once. The client applies it on the way
    /// in and again at the upload, and the second pass must not change the first one's answer.</summary>
    [Theory]
    [InlineData("image/jpg")]
    [InlineData("image/png")]
    [InlineData("image/webp")]
    [InlineData("anything else")]
    public void Normalizing_is_idempotent(string mime)
    {
        var once = AlbumArt.NormalizeMime(mime);
        Assert.Equal(once, AlbumArt.NormalizeMime(once));
    }

    // ── the disc label's own key ──

    /// <summary>
    /// The label printed on the record is a second picture of the same album, stored beside the cover
    /// under the caller's own art prefix. It must never land on the cover's key: whichever was written
    /// second would be the one the wall showed.
    /// </summary>
    [Fact]
    public void A_disc_label_is_not_stored_where_the_cover_is()
        => Assert.NotEqual(AlbumArt.KeyFor("Manu Chao", "Clandestino"),
                           AlbumArt.DiscKeyFor("Manu Chao", "Clandestino"));

    /// <summary>The server checks only the shape of a key (32 lower-case hex characters), so a
    /// disc key that is anything else is a 400 rather than a stored picture.</summary>
    [Fact]
    public void A_disc_key_has_the_shape_the_store_accepts()
    {
        var key = AlbumArt.DiscKeyFor("AC/DC", "Back in Black 🎵");

        Assert.Equal(32, key.Length);
        Assert.All(key, c => Assert.True(c is >= '0' and <= '9' or >= 'a' and <= 'f'));
    }

    /// <summary>Same normalisation as the cover's, and the same album-artist-then-artist fallback: the
    /// two pictures of one record have to agree about whose record it is, or one computer stores the
    /// label where the next one will not look for it.</summary>
    [Fact]
    public void A_disc_label_follows_the_covers_rules_about_who_the_album_belongs_to()
    {
        Assert.Equal(AlbumArt.DiscKeyFor("Manu Chao", "Clandestino"),
                     AlbumArt.DiscKeyFor("  MANU CHAO ", "clandestino"));

        Assert.Equal(AlbumArt.DiscKeyFor("Various Artists", "Now 22"),
                     AlbumArt.DiscKeyForTrack("Various Artists", "Snap!", "Now 22"));

        // A blank album artist is not an album artist. KeyForTrack handles the same trap.
        Assert.Equal(AlbumArt.DiscKeyFor("Someone", "Record"),
                     AlbumArt.DiscKeyForTrack("   ", "Someone", "Record"));
    }

    /// <summary>Pinned for the same reason the cover's is: changing it orphans every label already
    /// stored, silently, for everyone.</summary>
    [Fact]
    public void The_disc_hash_is_pinned()
        => Assert.Equal("ee1067d65d84ffa7e4c8b433b457284d",
                        AlbumArt.DiscKeyFor("Manu Chao", "Clandestino"));

    // ── what a picture actually is ──

    /// <summary>
    /// The type goes inside the presigned signature, so it has to be the truth rather than whatever
    /// named the file (a cover is written to the folder as cover.jpg whatever its format). A disc
    /// label is the case that matters: it is a transparent PNG, and if it is labelled as a JPEG it
    /// comes back without its transparency.
    /// </summary>
    [Fact]
    public void A_picture_is_typed_by_its_first_bytes()
    {
        Assert.Equal("image/png", AlbumArt.SniffMime([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2]));
        Assert.Equal("image/webp", AlbumArt.SniffMime(
            [(byte)'R', (byte)'I', (byte)'F', (byte)'F', 1, 2, 3, 4,
             (byte)'W', (byte)'E', (byte)'B', (byte)'P', 0]));
        Assert.Equal("image/jpeg", AlbumArt.SniffMime([0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4]));

        // Nothing recognisable is a JPEG, which is what the store signs for by default: a picture with
        // the wrong label still uploads, where a rejected one is an album with no art.
        Assert.Equal("image/jpeg", AlbumArt.SniffMime([]));
        Assert.Equal("image/jpeg", AlbumArt.SniffMime([1, 2, 3]));
    }

    // ── reading an album's pictures off its folder ──

    /// <summary>
    /// A record whose bytes are all in the cloud has no file to read art out of, and can still have a
    /// picture: one chosen for it is written into the album folder. Without this the cover stays on
    /// the computer where it was chosen and never reaches the phone.
    /// </summary>
    [Fact]
    public void An_album_folder_answers_for_a_record_with_no_files_here()
    {
        var folder = Directory.CreateTempSubdirectory("rpm-art").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(folder, "cover.jpg"), [0xFF, 0xD8, 0xFF, 0xE0, 9]);
            File.WriteAllBytes(Path.Combine(folder, "disc.png"), [0x89, 0x50, 0x4E, 0x47, 9]);

            var cover = AlbumArt.ForFolder(folder);
            Assert.NotNull(cover);
            Assert.Equal("image/jpeg", cover!.Mime);
            Assert.Equal(AlbumCover.SourceFolder, cover.Source);

            // PNG, and stored as PNG: the label is transparent and a JPEG would fill that in.
            var disc = AlbumArt.DiscForFolder(folder);
            Assert.NotNull(disc);
            Assert.Equal("image/png", disc!.Mime);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>A folder with no picture in it, and one that is not there at all, are both "no art".
    /// This is the normal case for most records, and never an error.</summary>
    [Fact]
    public void A_folder_with_nothing_in_it_is_no_art()
    {
        var folder = Directory.CreateTempSubdirectory("rpm-art").FullName;
        try
        {
            Assert.Null(AlbumArt.ForFolder(folder));
            Assert.Null(AlbumArt.DiscForFolder(folder));
            Assert.Null(AlbumArt.ForFolder(Path.Combine(folder, "not-here")));
            Assert.Null(AlbumArt.DiscForFolder(Path.Combine(folder, "not-here")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
