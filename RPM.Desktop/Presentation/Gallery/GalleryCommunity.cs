using RPM.Core.Cloud;
using RPM.Core.Library;
using RPM.Core.Metadata.Providers;

namespace RPM.Desktop.Presentation.Gallery;

/// <summary> The community half of the album page, made up: the shared entry for a record, the reviews
/// under it, the remarks beside them, and the row of suggestions along the foot.
///
/// <para>The album page has two halves with two sources. The left and middle come from the user's own
/// library. The right comes from the API. Only the second needs a server and an account. So the gallery
/// stands in for that half only. The page it fills is the real one, with the same view models, sorts,
/// paging and write paths. What is faked is the wire, not the screen.</para>
///
/// <para>It is a stand-in server rather than a fixed list of rows. The pools are made once per album
/// and then <b>kept</b>. So a vote sticks, a comment stays where it was posted, a rating moves the
/// average, and switching sort tabs re-orders the same reviews rather than dealing a fresh set. The
/// pools are deterministic, from the album's own name, so a record always opens the same way.</para>
/// </summary>
public static class GalleryCommunity
{
    /// <summary>Who the reader is in the harness. One review and one comment carry it, so the row that
    /// offers a delete rather than a report is on screen.</summary>
    public const string MeUserId = "gallery-me";

    private const string MeName = "You";

    // ── the stand-in store ──

    private static readonly Dictionary<string, List<RpmReview>> Reviews = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, List<RpmComment>> Comments = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, List<RpmWallEntry>> Walls = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, (string Artist, string Title, int Year, string? Catno)> Identities
        = new(StringComparer.Ordinal);

    private static long _nextCommentId = 1;
    private static long _nextWallId = 1;

    /// <summary>The entry for a record. Its reviews and comments are made the first time it is asked
    /// for. The numbers on it are counted from the pools, so they agree with what the page draws.
    /// </summary>
    public static RpmAlbum Album(string artist, string title, int year, string? catno)
    {
        var albumId = AlbumId(artist, title);
        Identities[albumId] = (artist, title, year, catno);

        if (!Reviews.ContainsKey(albumId))
        {
            var seed = Fnv(albumId);
            Reviews[albumId] = MintReviews(albumId, seed);
            Comments[albumId] = MintComments(albumId, seed);
        }

        return Album(albumId);
    }

    /// <summary>The same entry, re-counted after a write.</summary>
    public static RpmAlbum Album(string albumId)
    {
        var reviews = Reviews.TryGetValue(albumId, out var list) ? list : [];
        var comments = Comments.TryGetValue(albumId, out var said) ? said : [];
        var identity = Identities.TryGetValue(albumId, out var id) ? id : ("", "", 0, null);

        var rated = reviews.Where(r => r.Rating > 0).ToList();
        var average = rated.Count == 0 ? 0 : rated.Average(r => r.Stars);

        return new RpmAlbum(
            albumId, identity.Item1, identity.Item2, identity.Item3, identity.Item4,
            DiscogsReleaseId: null,
            ReviewCount: reviews.Count,
            CommentCount: comments.Count,
            AverageRating: Math.Round(average, 1),
            RatingCount: rated.Count);
    }

    /// <summary>One page of reviews in the sort the tabs asked for. The pool is ordered here, not dealt
    /// again. That is what makes the tabs a re-ordering of the same records.</summary>
    public static RpmReviewPage Page(string albumId, RpmReviewSort sort, int page, int pageSize)
    {
        var all = Reviews.TryGetValue(albumId, out var list) ? list : [];

        IEnumerable<RpmReview> ordered = sort switch
        {
            RpmReviewSort.Date => all.OrderByDescending(r => r.CreatedAt),
            RpmReviewSort.Length => all.OrderByDescending(r => r.Body.Length),
            RpmReviewSort.Positive => all.OrderByDescending(r => r.Rating).ThenByDescending(r => r.Score),
            RpmReviewSort.Negative => all.OrderBy(r => r.Rating).ThenByDescending(r => r.Score),
            _ => all.OrderByDescending(r => r.Score).ThenByDescending(r => r.CreatedAt),
        };

        var items = ordered.Skip((Math.Max(1, page) - 1) * pageSize).Take(pageSize).ToList();
        return new RpmReviewPage(items, page, pageSize, all.Count, sort.ToString().ToLowerInvariant());
    }

    public static IReadOnlyList<RpmComment> Said(string albumId)
        => Comments.TryGetValue(albumId, out var list) ? [.. list] : [];

    // ── writes, kept ──

    /// <summary>Casts a vote on another reader's review, and returns the row as the server would have
    /// left it. Null for a review that is not in the pool.</summary>
    public static RpmReview? Vote(string albumId, string userId, int vote)
    {
        if (!Reviews.TryGetValue(albumId, out var list)) return null;

        var index = list.FindIndex(r => r.UserId == userId);
        if (index < 0) return null;

        var review = list[index];
        var ups = review.Ups - (review.MyVote > 0 ? 1 : 0) + (vote > 0 ? 1 : 0);
        var downs = review.Downs - (review.MyVote < 0 ? 1 : 0) + (vote < 0 ? 1 : 0);

        var updated = review with { Ups = ups, Downs = downs, Score = ups - downs, MyVote = vote };
        list[index] = updated;
        return updated;
    }

    /// <summary>The reader's own verdict, written or rewritten. Ratings are in halves of a star, from 1
    /// to 10. The wall keeps them in that form.</summary>
    public static void WriteReview(string albumId, int rating, string? body)
    {
        if (!Reviews.TryGetValue(albumId, out var list)) return;

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var index = list.FindIndex(r => r.UserId == MeUserId);
        var text = string.IsNullOrWhiteSpace(body) ? "" : body.Trim();

        if (index >= 0)
        {
            list[index] = list[index] with { Rating = rating, Body = text, UpdatedAt = now };
            return;
        }

        list.Insert(0, new RpmReview(
            albumId, MeUserId, MeName, HasAvatar: false, rating, text,
            CreatedAt: now, UpdatedAt: now, Ups: 0, Downs: 0, Score: 0, MyVote: 0));
    }

    public static void DeleteMyReview(string albumId)
    {
        if (Reviews.TryGetValue(albumId, out var list))
            list.RemoveAll(r => r.UserId == MeUserId);
    }

    public static void AddComment(string albumId, string body)
    {
        if (!Comments.TryGetValue(albumId, out var list)) return;

        list.Add(new RpmComment(
            albumId, $"gallery-c{_nextCommentId++}", MeUserId, MeName, HasAvatar: false,
            body.Trim(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }

    public static void DeleteComment(string albumId, string commentId)
    {
        if (Comments.TryGetValue(albumId, out var list))
            list.RemoveAll(c => c.CommentId == commentId);
    }

    // ── artists and their walls ──

    /// <summary> The entry for an artist. Their wall is made the first time it is asked for, and it is
    /// never null. In the harness every artist has a wall, because a page drawn with an empty one would
    /// have its block missing. The review lines on the wall name the artist's own records, so the wall
    /// and the album list on the same page agree. </summary>
    public static RpmArtist Artist(ArtistWallItem artist)
    {
        var artistId = ArtistId(artist.Name);

        if (!Walls.ContainsKey(artistId))
        {
            // The album title is the last part of the folder. Every harness library files a record this
            // way: Artist/Album.
            var titles = artist.Own.Select(folder => folder[(folder.LastIndexOf('/') + 1)..]).ToList();
            Walls[artistId] = MintWall(artistId, Fnv(artistId), titles);
        }

        return new RpmArtist(artistId, artist.Name.Trim(), CreatedAt: 1);
    }

    /// <summary>What is on an artist's wall here, newest first, as the API answers it.</summary>
    public static IReadOnlyList<RpmWallEntry> Wall(string artistId)
        => Walls.TryGetValue(artistId, out var list)
            ? [.. list.OrderByDescending(e => e.CreatedAt)]
            : [];

    public static void AddWallPost(string artistId, string body)
    {
        if (!Walls.TryGetValue(artistId, out var list)) return;

        list.Add(new RpmWallEntry(
            artistId, $"gallery-w{_nextWallId++}", RpmWallEntry.PostKind, MeUserId, MeName,
            HasAvatar: false, body.Trim(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }

    public static void DeleteWallPost(string artistId, string entryId)
    {
        if (Walls.TryGetValue(artistId, out var list))
            list.RemoveAll(e => e.EntryId == entryId);
    }

    /// <summary> A wall dealt from the two pools, with both kinds of line mixed through it. One kind is
    /// what people said about the artist. The other is what the server would have written when they
    /// reviewed or rated one of the artist's records. One of the posts is the reader's own, for the row
    /// that offers to take it back down. </summary>
    /// <param name="titles">The artist's own records. Empty for an artist with none, in which case the
    /// review lines name titles from the pool.</param>
    private static List<RpmWallEntry> MintWall(string artistId, uint seed, IReadOnlyList<string> titles)
    {
        var said = 3 + (int)(seed % 4);
        var done = 2 + (int)((seed >> 4) % 4);
        var list = new List<RpmWallEntry>(said + done + 1);
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < said; i++)
        {
            var remark = WallSaid[(int)((seed >> 8) + i) % WallSaid.Length];
            list.Add(new RpmWallEntry(
                artistId, $"gallery-{artistId}-s{i}", RpmWallEntry.PostKind, remark.User, remark.Name,
                HasAvatar: false, remark.Body, now.AddDays(-remark.DaysAgo).ToUnixTimeMilliseconds()));
        }

        for (var i = 0; i < done; i++)
        {
            var verdict = WallVerdicts[(int)((seed >> 12) + i) % WallVerdicts.Length];
            list.Add(new RpmWallEntry(
                artistId, $"gallery-{artistId}-r{i}", RpmWallEntry.ReviewKind, verdict.User, verdict.Name,
                HasAvatar: false, "", now.AddDays(-verdict.DaysAgo).ToUnixTimeMilliseconds(),
                AlbumId: $"gallery-{artistId}-alb{i}",
                AlbumTitle: titles.Count > 0 ? titles[i % titles.Count] : verdict.Album,
                Rating: verdict.Rating, HasBody: verdict.HasBody));
        }

        list.Add(new RpmWallEntry(
            artistId, $"gallery-{artistId}-me", RpmWallEntry.PostKind, MeUserId, MeName,
            HasAvatar: false, "Third time seeing them and the new material held up live.",
            now.AddDays(-1).ToUnixTimeMilliseconds()));

        return list;
    }

    private static readonly (string User, string Name, int DaysAgo, string Body)[] WallSaid =
    [
        ("w-01", "Ines V.", 3, "Been listening since I was fifteen."),
        ("w-02", "vinylhoarder88", 5, "Nothing else they made sounds anything like the first record."),
        ("w-03", "Marlowe", 8, "Saw the tour twice. Worth every mile."),
        ("w-04", "halcyon_dust", 12, "The first three records are untouchable."),
        ("w-05", "Devon K.", 19, "Found a signed copy in a charity shop today."),
        ("w-06", "Ana Ferreira", 26, "The live records are where they make sense."),
    ];

    private static readonly (string User, string Name, int DaysAgo, string Album, int Rating, bool HasBody)[]
        WallVerdicts =
    [
        ("v-01", "Ovid", 2, "The Fall", 10, true),
        ("v-02", "Devon K.", 6, "Who Knows You Best?", 8, false),
        ("v-03", "Ines V.", 11, "The Fall", 9, false),
        ("v-04", "Marlowe", 17, "Long Way Down", 6, true),
        ("v-05", "Ana Ferreira", 23, "Long Way Down", 7, false),
    ];

    /// <summary>The id an artist resolves to here, folded the way the server folds a name. The same
    /// artist opened twice gives the same wall.</summary>
    private static string ArtistId(string name)
        => $"gallery-art-{Fnv(name.Trim().ToLowerInvariant()):x8}";

    // ── more like this ──

    /// <summary> What the Discogs half of the suggestions row would have answered. Half of them are
    /// records the demo library already holds, so the row is drawn the way it is in real use. Some
    /// cards open an album and some link out. The corner badge shows which is which. </summary>
    public static IReadOnlyList<ReleaseSearchResult> Similar(string artist, string album)
    {
        var seed = Fnv($"{artist}|{album}");
        var rotated = Suggestions.Skip((int)(seed % (uint)Suggestions.Length)).Concat(Suggestions);

        return
        [
            .. rotated
                .Where(s => !string.Equals(s.Album, album, StringComparison.OrdinalIgnoreCase))
                .Take(8)
                .Select((s, i) => new ReleaseSearchResult(
                    new ReleaseRef("discogs", $"gallery-{seed % 9000 + i}"),
                    s.Album, s.Artist, (uint)s.Year, $"{s.Year}", "GB", "Vinyl, LP, Album",
                    TrackCount: 10, Label: s.Label, Relevance: 0.9 - i * 0.05)),
        ];
    }

    private static readonly (string Artist, string Album, int Year, string Label)[] Suggestions =
    [
        // Owned by the demo library, so these come back as openable album cards.
        ("Boards of Canada", "Music Has the Right to Children", 1998, "Warp"),
        ("Justice", "Cross", 2007, "Ed Banger"),
        ("Röyksopp", "Melody A.M.", 2001, "Wall of Sound"),
        // Not owned: these are the cards that link out.
        ("Air", "Moon Safari", 1998, "Source"),
        ("Cassius", "1999", 1999, "Virgin"),
        ("LCD Soundsystem", "Sound of Silver", 2007, "DFA"),
        ("Kraftwerk", "Trans-Europe Express", 1977, "Kling Klang"),
        ("Fatboy Slim", "You've Come a Long Way, Baby", 1998, "Skint"),
        ("Stereolab", "Emperor Tomato Ketchup", 1996, "Duophonic"),
        ("Massive Attack", "Mezzanine", 1998, "Circa"),
    ];

    // ── the pressing, for the seed ──

    /// <summary>Catalogue number, release date and Discogs styles for the demo shelf. The album page
    /// reads these from the album's own row. The harness seeds them into the library, rather than
    /// answering a lookup with them.</summary>
    public static (string Catno, string Released, string ReleaseId) Pressing(string artist, string album)
        => Pressings.TryGetValue($"{artist}|{album}", out var known)
            ? known
            : ($"RPM {Fnv(album) % 9000 + 1000}", $"{1990 + (int)(Fnv(artist) % 30)}", "249504");

    private static readonly Dictionary<string, (string Catno, string Released, string ReleaseId)> Pressings =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Daft Punk|Discovery"] = ("7243 8 49606 1 8", "2001-03-12", "20736"),
            ["Boards of Canada|Music Has the Right to Children"] = ("WARP CD 55", "1998-04-20", "22077"),
            ["Aphex Twin|Selected Ambient Works 85-92"] = ("AMB LP 3922", "1992-11-09", "1145"),
            ["Justice|Cross"] = ("BEC 5772217", "2007-06-11", "1013478"),
            ["Röyksopp|Melody A.M."] = ("WALLLP 020", "2001-10-08", "116889"),
            ["The Chemical Brothers|Surrender"] = ("XDUSTLP 4", "1999-06-21", "20539"),
            ["ABBA|Super Trouper"] = ("POLS 322", "1980-11-03", "1359437"),
        };

    // ── minting ──

    /// <param name="Rating">Halves of a star, 1 to 10, as the wire carries it.</param>
    /// <param name="Ups">Before the reader's own vote, which is applied on top.</param>
    private sealed record Voice(string User, string Name, int Rating, int Ups, int Downs, int DaysAgo, string Body);

    /// <summary> The pool that every album's reviews are dealt from: long ones and one-liners, glowing
    /// and sour. The column then has the shape of a real one, rather than twenty rows of the same
    /// length. None of them names a genre, an instrument or a number of years. Every record on every
    /// harness shelf is dealt from this pool, so a 1976 country album and a 2008 electronic one can
    /// both use it. </summary>
    private static readonly Voice[] Voices =
    [
        new("u-01", "Marlowe", 10, 214, 9, 12,
            "All these years on and the thing still sounds like it was mastered next week. What gets "
            + "me is how little of it is showing off. Every part is doing a job, nothing is there to "
            + "fill space, and when the chorus of the title track finally lands you have been set up "
            + "for it for two full minutes.\n\nSide two is the reason I keep buying "
            + "pressings of this. It never quite resolves, and it never wants to."),
        new("u-02", "quietstorm", 9, 168, 14, 34,
            "The first four tracks are perfect and the rest is merely very good, which is a complaint "
            + "only if you have heard the first four."),
        new("u-03", "Ines V.", 10, 141, 6, 61,
            "I put this on for people who tell me they do not like this kind of music. It has never once "
            + "failed. There is a warmth in the low end that most records in this corner never bother "
            + "to look for."),
        new("u-04", "bitrot", 6, 97, 41, 8,
            "Overrated by a full star. The singles are untouchable, sure, but there is a stretch in the "
            + "middle where the ideas run out and the record keeps going anyway. I would rather have "
            + "a tight thirty five minutes than a padded sixty."),
        new("u-05", "Tomas Reiner", 9, 88, 5, 120,
            "Bought the original pressing the week it came out, lost it in a move, and spent four years "
            + "finding another. Worth every bit of the hunt. The reissue is louder and I do not think it "
            + "is better."),
        new("u-06", "halcyon_dust", 8, 74, 11, 19,
            "Best listened to end to end, late, with the lights off. Skipping tracks on this one is like "
            + "reading every third page."),
        new("u-07", "Priya N.", 10, 66, 3, 45,
            "The sequencing is the achievement. Anyone can write two good songs. Deciding which one goes "
            + "second is the hard part, and this record gets it right ten times running."),
        new("u-08", "Old Growth", 7, 52, 18, 200,
            "Good, not holy. It arrived at exactly the right moment and a lot of what people hear in it "
            + "is the moment. Still, track three is worth the price on its own."),
        new("u-09", "vinylhoarder88", 9, 44, 7, 15,
            "Pressing note for anyone shopping: the 2013 repress has noticeably more surface noise on "
            + "side two. The first press is quieter and the low end sits deeper. Both play fine."),
        new("u-10", "Ana Ferreira", 8, 37, 9, 90,
            "Grew on me slowly. First listen I thought it was cold. Tenth listen I understood that the "
            + "coldness is the point, and that there is a lot of feeling under it that does not "
            + "announce itself."),
        new("u-11", "mono_no", 4, 31, 55, 26,
            "I have tried. Everyone I trust loves this and I hear a very well made record that leaves me "
            + "nothing to hold on to. No hard feelings, it is clearly not aimed at me."),
        new("u-12", "Devon K.", 10, 29, 2, 7,
            "Still the one I reach for when I want to remember why I started collecting."),
        new("u-13", "sable", 7, 22, 8, 150,
            "The remaster fixed the muddy low mids and flattened the dynamics doing it. Pick your "
            + "poison. I keep both and play the old one."),
        new("u-14", "Hedda L.", 9, 19, 4, 3,
            "Came to it years late and it did not matter at all. Some records do not date because "
            + "they were never chasing anything."),
        new("u-15", "circuit_bent", 8, 16, 6, 55,
            "Track seven is the one nobody talks about and it is the best thing here."),
        new("u-16", "Roland J.", 5, 12, 21, 77,
            "Two great songs, a lot of filler, and a reputation doing the rest of the work. I will keep "
            + "the copy but I will not pretend."),
        new("u-17", "delta wave", 10, 11, 1, 41,
            "Perfect from the first bar. There is nothing here I would change and I have had years to "
            + "think about it."),
        new("u-18", "Mira", 8, 9, 3, 5,
            "My partner puts this on every Sunday morning and I have not got tired of it yet. That is a "
            + "higher bar than any review score."),
        new("u-19", "grainstore", 7, 7, 2, 110,
            "Handsome record, beautifully made, slightly airless. Play it loud and the problem goes "
            + "away."),
        new("u-20", "Ovid", 9, 6, 1, 22,
            "The closer earns every second of its length, which is not something I say often."),
        new("u-21", "nn_seven", 6, 4, 5, 68,
            "Fine. Good, even. But the version everybody grew up with is the single edit, and the album "
            + "cut wanders."),
        new("u-22", "Solveig", 10, 3, 0, 2,
            "First record I ever bought with my own money. Cannot be objective and will not try."),
        new("u-23", "kite string", 8, 2, 1, 33,
            "Deceptively simple. Try to make anything that sounds like this and you find out fast how "
            + "much is going on."),
        new("u-24", "Bram", 9, 1, 0, 88,
            "The pressing I have is warped and I still play it every month. Says enough."),
    ];

    private static readonly (string User, string Name, int DaysAgo, string Body)[] Remarks =
    [
        ("c-01", "Ines V.", 4, "Anyone know if the Japanese pressing has the extra track?"),
        ("c-02", "vinylhoarder88", 6, "It does, and it is worth hearing once. Not worth the price."),
        ("c-03", "Marlowe", 9, "Playing this right now and the room sounds better for it."),
        ("c-04", "halcyon_dust", 14, "Sleeve art is still the best thing in my whole shelf."),
        ("c-05", "Devon K.", 21, "Second half is criminally underrated."),
        ("c-06", "Ana Ferreira", 30, "Whoever mastered the reissue owes us an apology."),
        ("c-07", "Ovid", 44, "Put this on at a party and three people asked what it was."),
    ];

    private static List<RpmReview> MintReviews(string albumId, uint seed)
    {
        // How many, and where in the pool the deal starts. Both come from the album's own name, so two
        // records never open with the same wall, and the same record always opens with its own.
        var count = 8 + (int)(seed % 17);
        var start = (int)(seed >> 8) % Voices.Length;
        var shift = (int)(seed >> 16) % 3 - 1;   // this record is a little better or worse liked

        var list = new List<RpmReview>(count);
        for (var i = 0; i < count; i++)
        {
            var voice = Voices[(start + i) % Voices.Length];
            var rating = Math.Clamp(voice.Rating + shift, 1, 10);
            var ups = voice.Ups;
            var downs = voice.Downs;

            // Two rows carry the reader's own vote, so both lit arrows are on screen at once.
            var myVote = i == 1 ? 1 : i == 4 ? -1 : 0;
            var written = DateTimeOffset.UtcNow.AddDays(-voice.DaysAgo).ToUnixTimeMilliseconds();

            list.Add(new RpmReview(
                albumId, voice.User, voice.Name, HasAvatar: false, rating, voice.Body,
                CreatedAt: written,
                // One review is rewritten, so the "edited" mark has something to show.
                UpdatedAt: i == 2 ? written + 172_800_000 : written,
                Ups: ups + (myVote > 0 ? 1 : 0),
                Downs: downs + (myVote < 0 ? 1 : 0),
                Score: ups - downs + myVote,
                MyVote: myVote));
        }

        // The reader's own, so the row with a delete on it rather than a report is always there.
        var mine = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds();
        list.Add(new RpmReview(
            albumId, MeUserId, MeName, HasAvatar: false, Rating: 9,
            "Filed under the ones I will still be playing in ten years. The middle stretch took me a "
            + "while, and now it is the part I wait for.",
            CreatedAt: mine, UpdatedAt: mine, Ups: 5, Downs: 0, Score: 5, MyVote: 0));

        return list;
    }

    private static List<RpmComment> MintComments(string albumId, uint seed)
    {
        var count = 3 + (int)(seed % 5);
        var start = (int)(seed >> 4) % Remarks.Length;

        var list = new List<RpmComment>(count);
        for (var i = 0; i < count; i++)
        {
            var remark = Remarks[(start + i) % Remarks.Length];
            list.Add(new RpmComment(
                albumId, $"gallery-{albumId}-{i}", remark.User, remark.Name, HasAvatar: false,
                remark.Body, DateTimeOffset.UtcNow.AddDays(-remark.DaysAgo).ToUnixTimeMilliseconds()));
        }

        // One of the reader's own, for the row that offers to take it back down.
        list.Add(new RpmComment(
            albumId, $"gallery-{albumId}-me", MeUserId, MeName, HasAvatar: false,
            "Original press, and the run-out has a message scratched into it. Small thing, made my week.",
            DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeMilliseconds()));

        return list;
    }

    /// <summary>The id a record resolves to here. Case and spacing are folded out of it, so the same
    /// album opened from the wall and from the list gives the same entry.</summary>
    private static string AlbumId(string artist, string title)
        => $"gallery-{Fnv($"{artist.Trim().ToLowerInvariant()}|{title.Trim().ToLowerInvariant()}"):x8}";

    /// <summary>A stable hash, for the same reason <see cref="GalleryArt"/> has one. The framework's
    /// hash is randomised per process, and a record that deals itself a different wall on every run is
    /// no use for looking at.</summary>
    private static uint Fnv(string s)
    {
        uint hash = 2166136261;
        foreach (var c in s)
        {
            hash ^= c;
            hash *= 16777619;
        }
        return hash;
    }
}
