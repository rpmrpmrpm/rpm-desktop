namespace RPM.Core.Library;

/// <summary>One song the hourly mix may draw on, as the track rows hand it over.</summary>
/// <param name="RelativePath">Where the song is. The mix is a list of these paths.</param>
/// <param name="Folder">The song's record (album), so that records can take turns.</param>
/// <param name="Favorite">Whether the listener marked it as a favourite.</param>
/// <param name="Plays">How many times it has been played, all-time.</param>
/// <param name="LastPlayedUtcTicks">When it was last played. It breaks ties between songs with the
/// same score: the song played most recently comes first.</param>
public readonly record struct MixSong(
    string RelativePath, string Folder, bool Favorite, int Plays, long LastPlayedUtcTicks);

/// <summary>
/// The songs to put on right now: what the listener loves, what they play, leaning towards the kinds
/// of music they play at THIS time of day.
///
/// <para><b>Three facts are folded into one ranked list. None of them is a filter.</b> The other
/// generated lists are yes or no questions (favourite, played this month, calm for its genre). This
/// one is a ranking, because the three facts can disagree: a favourite in the wrong kind of music for
/// the hour is still a favourite, and a song played forty times in a genre they play all evening is
/// worth more than either fact alone. As filters they would be an AND of three conditions, which on a
/// real library is usually empty.</para>
///
/// <para><b>A song must belong to the listener before it can fit.</b> A song with no favourite mark
/// and no plays is not in the pool at all, whatever the hour says. The genre weight only decides the
/// ORDER of music the listener has already shown they want. Without this rule the list would be a
/// genre shuffle. This is the one hard rule.</para>
///
/// <para><b>It lives here and not in the view or the rule</b> because it is arithmetic over facts,
/// and arithmetic is easy to get wrong. The database hands it rows and it hands back paths.</para>
/// </summary>
public static class HourlyMix
{
    /// <summary>What a favourite mark is worth. It is the clearest statement the listener makes about
    /// a song, and the only one of the three that is made on purpose.</summary>
    public const double Loved = 1.0;

    /// <summary>What playing a song a lot is worth, at most. It is a little less than a favourite: it
    /// is the same opinion, but not stated, and a song can be played often for reasons other than
    /// liking it.</summary>
    public const double Played = 0.9;

    /// <summary>What fitting the hour is worth, at most. It is multiplied by the genre's share, so a
    /// genre that is the whole of someone's evening adds all of this and a genre that is a tenth of it
    /// adds a tenth.
    ///
    /// <para>This is the largest of the three on purpose. Favourites and much-played songs already
    /// have their own lists. What this list adds is the time of day.</para></summary>
    public const double Fits = 1.3;

    /// <summary>How many plays count as "played as much as anything gets played". Above this a song
    /// scores no higher: the difference between forty plays and four hundred says how long the song
    /// has been in the library, not how much it is wanted now.</summary>
    public const int PlaysForFull = 10;

    /// <summary>
    /// The mix, best first and then spread so the records take turns.
    /// </summary>
    /// <param name="songs">Every song the listener has marked as a favourite or played. Songs with
    /// neither are not candidates and the caller should not send them. One that arrives anyway scores
    /// nothing and is dropped here too, so the rule cannot be broken by widening a query.</param>
    /// <param name="genreByFolder">The genre of each record, folded the app's own way:
    /// <see cref="MoodBands.GenreByFolder"/>, the same table the listening profile was built with.
    /// It is passed in and not read from a tag, because otherwise this code and the profile could
    /// disagree about the genre of a record and the weights would land on the wrong songs.</param>
    /// <param name="genresNow">The genres the listener plays at this time of day, with each genre's
    /// share: <see cref="ListeningProfile.GenresAt"/>. Empty is normal and not a failure (a library
    /// with no genre tags, or no history for this part of the day). The list is then ranked on
    /// favourite marks and play counts alone.</param>
    /// <param name="count">How many songs to hand back.</param>
    /// <param name="mostFromOneRecord">The most songs the list may take from one record. It is a
    /// parameter and not read from <see cref="SmartPlaylists.MostFromOneRecord"/> here, so a test can
    /// choose its own cap.</param>
    public static IReadOnlyList<string> Songs(
        IReadOnlyList<MixSong> songs,
        IReadOnlyDictionary<string, string> genreByFolder,
        IReadOnlyList<GenreShare> genresNow,
        int count,
        int mostFromOneRecord)
    {
        ArgumentNullException.ThrowIfNull(songs);
        ArgumentNullException.ThrowIfNull(genreByFolder);
        ArgumentNullException.ThrowIfNull(genresNow);

        if (count <= 0 || mostFromOneRecord <= 0)
            return [];

        var shares = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var share in genresNow)
            shares[share.Genre] = share.Share;

        var scored = new List<(MixSong Song, double Score)>();
        foreach (var song in songs)
        {
            if (!song.Favorite && song.Plays <= 0)
                continue;   // not the listener's song, whatever the hour says

            var score = (song.Favorite ? Loved : 0)
                      + (Played * Math.Min(1.0, song.Plays / (double)PlaysForFull));

            if (genreByFolder.TryGetValue(song.Folder, out var genre)
                && shares.TryGetValue(genre, out var fit))
                score += Fits * fit;

            scored.Add((song, score));
        }

        scored.Sort((a, b) =>
        {
            var byScore = b.Score.CompareTo(a.Score);
            if (byScore != 0)
                return byScore;

            // Among songs with the same score, the one played most recently comes first, then the path.
            // This way two reads of an unchanged library return the same order.
            var byPlayed = b.Song.LastPlayedUtcTicks.CompareTo(a.Song.LastPlayedUtcTicks);
            return byPlayed != 0
                ? byPlayed
                : string.Compare(a.Song.RelativePath, b.Song.RelativePath, StringComparison.Ordinal);
        });

        // The cap is applied BEFORE the turns, not after. The cap decides which songs are in the list
        // and the turns decide where they sit. In the other order the turns would spread songs that
        // the cap then removes, and the list would be shorter than requested.
        var held = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<MixSong>(count);
        foreach (var (song, _) in scored)
        {
            var seen = held.GetValueOrDefault(song.Folder);
            if (seen >= mostFromOneRecord)
                continue;

            held[song.Folder] = seen + 1;
            kept.Add(song);

            if (kept.Count == count)
                break;
        }

        // Spread the records, so a listener who loves three records does not get all of the first one
        // before the second starts. A ranking of one library tends to group by album, and this is the
        // same turn-taking used for the decade charts.
        return [.. RecordTurns.Taken(kept, song => song.Folder).Select(song => song.RelativePath)];
    }
}
