using System.Text.Json;
using System.Text.Json.Serialization;
using RPM.Core.Metadata.Providers;

namespace RPM.Core.Library;

/// <summary>The four moods a listener can make a list of their own from, by the names the window shows.</summary>
public enum MoodRecipeMood
{
    Calm,
    Dancefloor,
    Adrenaline,
    Focus,
}

/// <summary>
/// A mood playlist the listener made: a mood, and the genres, decades and artists to narrow it to. What it is drawn
/// from is the mood's own band (<see cref="MoodShelf"/>), narrowed by the three lists; an empty list is no narrowing
/// on that axis, and says so with <see cref="Everything"/> rather than by a missing argument.
///
/// <para>Kept beside the playlist it filled, as a library note (<see cref="LibraryDatabase.MoodRecipeOf"/>), so the
/// window can open it again and fill it again. The playlist itself is an ordinary one: it shows, plays, syncs and
/// takes a cover like any other.</para>
/// </summary>
public sealed record MoodRecipe(
    [property: JsonPropertyName("mood")] MoodRecipeMood Mood,
    [property: JsonPropertyName("genres")] IReadOnlyList<string> Genres,
    [property: JsonPropertyName("decades")] IReadOnlyList<int> Decades,
    [property: JsonPropertyName("artists")] IReadOnlyList<string> Artists,
    [property: JsonPropertyName("cover")] MoodCover Cover)
{
    /// <summary>No narrowing on an axis: every genre, every decade or every act.</summary>
    public static readonly IReadOnlyList<string> Everything = [];

    /// <summary>The band the mood is read from. No fallback arm: a new mood is named here or does not compile.</summary>
    public SmartPlaylistKind Kind => Mood switch
    {
        MoodRecipeMood.Calm => SmartPlaylistKind.Calm,
        MoodRecipeMood.Dancefloor => SmartPlaylistKind.Danceable,
        MoodRecipeMood.Adrenaline => SmartPlaylistKind.Energetic,
        MoodRecipeMood.Focus => SmartPlaylistKind.Focus,
    };

    /// <summary>The decades the window offers, by their first year. Wider than <see cref="SmartPlaylists.Decades"/>,
    /// which are the ones broken out as lists of their own: a recipe's decades are a filter, and any decade the shelf
    /// holds can be one.</summary>
    public static readonly IReadOnlyList<int> DecadesOffered = [1950, 1960, 1970, 1980, 1990, 2000, 2010, 2020];

    /// <summary>How many songs one filling holds: the mood lists' own length, doubled, because a list somebody made
    /// is kept and played rather than refreshed.</summary>
    public const int Songs = 100;

    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>The recipe written by <see cref="ToJson"/>, or null for anything that is not one.</summary>
    public static MoodRecipe? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            var read = JsonSerializer.Deserialize<MoodRecipe>(json, Json);
            return read is null ? null : read with
            {
                Genres = read.Genres ?? Everything,
                Decades = read.Decades ?? [],
                Artists = read.Artists ?? Everything,
                // A recipe with no cover wears the mood's own picture, the one the app's lists of that mood wear.
                Cover = read.Cover ?? MoodCover.OfMood,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Whether a song's credit names one of <see cref="Artists"/>: the credit as written, or one of the acts
    /// it splits into (<see cref="ArtistCredit.Split"/>), so "A feat. B" is in a list of B's.</summary>
    internal bool NamesActOf(string credit)
    {
        if (Artists.Count == 0)
            return true;
        if (Artists.Any(a => string.Equals(a, credit, StringComparison.OrdinalIgnoreCase)))
            return true;
        var acts = ArtistCredit.Split(credit);
        return acts.Any(act => Artists.Any(a => string.Equals(a, act, StringComparison.OrdinalIgnoreCase)));
    }
}

/// <summary>Where the picture under a mood playlist's frame comes from. Every mood playlist wears the frame the
/// generated lists wear (its name and the rpm mark, <c>CoverOverlay</c>); this is only what shows through it.
/// </summary>
public enum MoodCoverSource
{
    /// <summary>The picture the app's own list of this mood wears. The default.</summary>
    Mood,

    /// <summary>Up to four of the list's own records, laid out as a hand-made playlist's are.</summary>
    Records,

    /// <summary>A picture from the listener's computer, kept unframed beside the cover so a rename can frame it
    /// again.</summary>
    Picture,

    /// <summary>One of the made figures.</summary>
    Figure,
}

/// <summary>The picture under a mood playlist's frame, and what it needs to be drawn again.</summary>
/// <param name="Folders">The records, in the order chosen, for <see cref="MoodCoverSource.Records"/>; empty otherwise.</param>
/// <param name="Style">Which made figure, for <see cref="MoodCoverSource.Figure"/>.</param>
/// <param name="Seed">The roll that colours it, for <see cref="MoodCoverSource.Figure"/>.</param>
public sealed record MoodCover(
    [property: JsonPropertyName("source")] MoodCoverSource Source,
    [property: JsonPropertyName("folders")] IReadOnlyList<string> Folders,
    [property: JsonPropertyName("style")] int Style,
    [property: JsonPropertyName("seed")] uint Seed)
{
    public static readonly MoodCover OfMood = new(MoodCoverSource.Mood, [], 0, 0);
    public static readonly MoodCover OfPicture = new(MoodCoverSource.Picture, [], 0, 0);

    public static MoodCover OfRecords(IReadOnlyList<string> folders) => new(MoodCoverSource.Records, [.. folders], 0, 0);

    public static MoodCover OfFigure(int style, uint seed) => new(MoodCoverSource.Figure, [], style, seed);
}
