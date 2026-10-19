using RPM.Core.Metadata.Providers;

namespace RPM.Core.Library;

/// <summary>
/// What a refusal is about: one act, or one record.
///
/// <para><b>A type, not two tables or a boolean.</b> The two cases are the same statement made about
/// different subjects. Code that stores, reads or applies a refusal handles both together. The row is
/// keyed by the pair, the SQL asks both in one place, and the pool tests both in one pass. Two tables
/// would mean one rule written in two files. A boolean would mean "true means album", which a reader
/// would have to look up.</para>
/// </summary>
public enum RecommendScope
{
    /// <summary>Everything credited to one act. The credit is the album artist where a record has one, and
    /// the track artist otherwise. This is the same string the per-artist cap counts by, so a listener who
    /// refuses an act refuses exactly the songs that the cap groups together.</summary>
    Artist,

    /// <summary>Everything on one record, named by its folder. This is what a record is in the local copy
    /// of the library, and it is the same key that <see cref="MoodList"/> caps by.</summary>
    Album,
}

/// <summary>
/// Every act and record that the listener has told a generated list to stop offering. It is read once,
/// and then asked about each song.
///
/// <para><b>Read once, like the likes.</b> A cut asks about every song it holds. A query per song would
/// mean one round trip per row. See <see cref="LibraryDatabase.LikedByKind"/>, which is read beside this
/// one for the same reason.</para>
///
/// <para><b>Asked for ONE kind at a time, because a refusal is about one kind.</b> The rule for each song
/// follows <c>LibraryDatabase.NotTurnedDown</c>. An act refused for the dancefloor is removed from
/// Dancefloor and from every genre and decade list cut from it. It is still offered by Chill and Focus,
/// as before. A listener may not want an act at a party, but may still want them to fall asleep to. One
/// press must not say both.</para>
///
/// <para>This class does not use the database, so a test can build one from fixed input. The caller reads
/// the rows, and this class answers the question.</para>
/// </summary>
public sealed class RecommendRefusals
{
    private readonly HashSet<(RecommendScope Scope, string Subject, SmartPlaylistKind Kind)> _said;

    /// <summary>Nobody has refused anything. This is the case for almost every collection. It is handed out
    /// instead of a null, so callers never have to test for null.</summary>
    public static readonly RecommendRefusals None = new([]);

    public RecommendRefusals(
        IEnumerable<(RecommendScope Scope, string Subject, SmartPlaylistKind Kind)> said)
    {
        ArgumentNullException.ThrowIfNull(said);
        _said = new HashSet<(RecommendScope, string, SmartPlaylistKind)>(
            said.Select(s => (s.Scope, Fold(s.Subject), s.Kind)));
    }

    /// <summary>Whether anything has been refused at all. This is a cheap check, so a collection that
    /// nobody has refused anything from does no work per song.</summary>
    public bool Any => _said.Count > 0;

    /// <summary>
    /// Whether a list of this kind may still offer a song on this record, by this act.
    ///
    /// <para>Both are checked, because a refusal is about a subject, and a song has two subjects. A refused
    /// act also excludes their records. A refused record does not exclude the act.</para>
    /// </summary>
    /// <param name="artist">The credit, as <see cref="MoodList.Song.Artist"/> carries it. An empty credit
    /// is nobody, not one act with a hundred songs. The per-artist cap reads it the same way, for the
    /// same reason: a shelf of untagged files is not one artist.</param>
    public bool Allows(SmartPlaylistKind kind, string? folder, string? artist)
    {
        if (_said.Count == 0)
            return true;

        if (!string.IsNullOrWhiteSpace(artist)
            && _said.Contains((RecommendScope.Artist, Fold(artist), kind)))
            return false;

        return string.IsNullOrWhiteSpace(folder)
            || !_said.Contains((RecommendScope.Album, Fold(folder), kind));
    }

    /// <summary>Whether one subject has been refused for one kind. The menu asks this to decide how to word
    /// its entry.</summary>
    public bool Refused(RecommendScope scope, string? subject, SmartPlaylistKind kind)
        => !string.IsNullOrWhiteSpace(subject) && _said.Contains((scope, Fold(subject), kind));

    /// <summary>
    /// Who a song is BY, for the purpose of refusing them: the record's credit where it has one, and the
    /// track's otherwise.
    ///
    /// <para><b>Written here because three places need the same answer.</b> The database composes it this
    /// way when it reads the moods (<c>LibraryDatabase.QueryMeasuredMoods</c>). The pool caps by the
    /// string that comes out of this. The menu must write a refusal under the same name, or the press
    /// does nothing.</para>
    ///
    /// <para><b>Except on a compilation, where the album credit is not an act.</b> "Various Artists"
    /// marks a record with no single act. Reading it as an act is the same mistake that
    /// <c>MoodPool.Window</c> refuses to make with an empty credit. A name that stands for nobody must
    /// not be capped as if it stood for somebody. Without this rule, compilations collapse into one or
    /// two credits such as "Various Artists", and a cap per credit then leaves almost no room for them in
    /// the generated lists. On one library, 1,202 measured tracks from 45 compilations (by 883 different
    /// acts) shared two credits. With a cap of five for each credit, those 45 records competed for ten
    /// places in every list.</para>
    ///
    /// <para>So a compilation falls through to the track's own artist. That is the act a listener means
    /// when they refuse one. A compilation track with no artist of its own names nobody. It gets the
    /// empty credit, which says so.</para>
    ///
    /// <para>The other reading is the SQL in <c>LibraryDatabase.Offerable</c>. That SQL cannot call this
    /// method, and it says so where it is written.</para>
    /// </summary>
    public static string CreditOf(string? albumArtist, string? artist) =>
        !string.IsNullOrWhiteSpace(albumArtist) && !ArtistCredit.IsVariousArtists(albumArtist)
            ? albumArtist.Trim()
        : !string.IsNullOrWhiteSpace(artist) ? artist.Trim()
        : "";

    /// <summary>
    /// The one spelling a subject is held under.
    ///
    /// <para>Case-folded and trimmed. This is done here, not left to a database collation. The same rule
    /// applies to the folder, which is keyed case-insensitively, and to the credit, which is a tag that
    /// someone typed. "Beyoncé" and "beyoncé" are one act. The database column is NOCASE for the same
    /// reason, so a row written by one and read by the other still matches.</para>
    /// </summary>
    private static string Fold(string? subject) => (subject ?? "").Trim().ToLowerInvariant();
}
