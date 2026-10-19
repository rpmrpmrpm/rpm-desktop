using RPM.Core.Library;

namespace RPM.Core.Metadata.Providers;

/// <summary>What a generated playlist should search for.</summary>
/// <param name="Query">The words. These are never the playlist's own name. See
/// <see cref="CoverLooks.Look"/> for the reason.</param>
public readonly record struct CoverLook(string Query);

/// <summary>
/// What each generated playlist searches for.
///
/// <para><b>Nothing in the app searches at runtime. This table still makes the choice.</b> The pictures are
/// picked once and shipped, so they do not depend on a search key at run time. See
/// <c>scripts/playlist-photos.json</c>. Each entry there carries the words from this table that found it.
/// So a line changed here means a picture that no longer matches what its list asks for.
/// <c>PlaylistPhotoTests</c> checks this.</para>
///
/// <para><b>No museum source.</b> The Metropolitan Museum of Art is not used. Searched one word at a time,
/// it returns four brown nineteenth-century canvases that look like one picture at cover size.</para>
/// </summary>
public static class CoverLooks
{
    /// <summary>
    /// What one generated playlist searches for: its own words where the pair has them, and its mood's
    /// words where it does not.
    ///
    /// <para>Only the four moods have a look at all. The lists about listening describe a shelf, not a
    /// feeling. There is no honest picture search for "songs you have not played".</para>
    /// </summary>
    public static CoverLook? Look(SmartPlaylistId id)
    {
        if (Mood(id.Kind) is not { } mood)
            return null;

        if (id.Decade > 0)
            return InThe(id.Kind) is { } scene ? new CoverLook($"{id.Decade}s {scene}") : null;

        return id.Genre is { } genre && Within.TryGetValue((id.Kind, genre), out var words)
            ? new CoverLook(words)
            : new CoverLook(mood);
    }

    /// <summary>
    /// What a mood looks like in a given decade, once the decade itself is put in front of it.
    ///
    /// <para><b>Composed, not written out for each decade.</b> A genre needs a table, because no rule turns
    /// "Danceable" and "Blues" into "roadhouse bar interior night". A decade needs no table, because the
    /// decade is the search term. The image index holds many photographs tagged by period. "1970s" in front
    /// of a plain scene is a query that works well. Eighteen lines that say the same three things with a
    /// different number in front would be a table nobody could keep correct.</para>
    ///
    /// <para>The scenes carry a period on purpose, such as a room, a stage or a floor. Those are what a
    /// decade changes the look of. "1970s drifting clouds" would be a search about clouds.
    /// </para>
    ///
    /// <para>Null for a kind with no per-decade form, which is <see cref="SmartPlaylistKind.Sleep"/>. It
    /// has no decade rows (see <see cref="SmartPlaylists.BrokenOutByEra"/>), so nothing should ask. An
    /// answer with a picture would invent a list.</para>
    /// </summary>
    private static string? InThe(SmartPlaylistKind kind) => kind switch
    {
        SmartPlaylistKind.Calm => "living room interior",
        SmartPlaylistKind.Energetic => "concert stage",
        SmartPlaylistKind.Danceable => "dancefloor lights",
        SmartPlaylistKind.Focus => "desk lamp at night",

        // The decade's own list is a chart, not a mood. A shop full of the records people were buying shows
        // that.
        SmartPlaylistKind.Decade => "record shop interior",

        // The room the music is written for. Not a musician and not an instrument. Keys are the one thing
        // every classical record has, and a picture of an empty hall never shows a face.
        SmartPlaylistKind.Classical => "empty concert hall seats",
        _ => null,
    };

    /// <summary>
    /// What a mood asks for when there is no genre under it. These are the pooled list's own words, and the
    /// fallback for any pair that <see cref="Within"/> has no line for.
    ///
    /// <para>A genre added to <see cref="Metadata.Genres.All"/> later reads as its mood, not as
    /// nothing.</para>
    /// </summary>
    private static string? Mood(SmartPlaylistKind kind) => kind switch
    {
        SmartPlaylistKind.Calm => "calm sea",
        SmartPlaylistKind.Energetic => "storm",
        SmartPlaylistKind.Danceable => "club dance floor lights",
        SmartPlaylistKind.Focus => "still lake morning mist",
        SmartPlaylistKind.Sleep => "moonlight",

        // A decade always carries its own number, and never reaches this. Look answers the decade case
        // first. The line exists so that a kind with no words is a decision, not a gap.
        SmartPlaylistKind.Decade => "record shop interior",

        // The room the music is written for. Not a musician and not an instrument. Keys are the one thing
        // every classical record has, and a picture of an empty hall never shows a face.
        SmartPlaylistKind.Classical => "empty concert hall seats",
        _ => null,
    };

    /// <summary>
    /// What each mood-and-genre pair searches for.
    ///
    /// <para><b>Never the playlist's own name.</b> Every genre this app files under is also an ordinary
    /// English noun. Searching "Rock" returns geology, "Metal" returns armour, and "Folk", "House" and
    /// "Garage" are no better. So no line here uses its own genre's word, or either half of a slashed
    /// genre. The words describe the scene the pair suggests. The genre chose the scene. It is not what is
    /// searched for.</para>
    ///
    /// <para><b>Written out for each pair, because there is no formula.</b> A long table takes more typing
    /// than a rule. But no rule produces "juke joint dancing" from "Danceable" and "Blues". The table has
    /// the same shape, for the same reason, as the title table in <c>SmartPlaylistFace</c>. It is
    /// deliberately NOT that table. "Slow Burn" as a search returns photographs of fire.</para>
    ///
    /// <para><b>Concrete and visual, two or three words.</b> A vague query matches vaguely. An abstract
    /// one, such as "energy" or "warmth", matches nothing a person would recognise. Every line names
    /// something that can be photographed.</para>
    ///
    /// <para><b>Every line was run against the live index.</b> A query that reads well does not always
    /// match well. The only way to tell is to look at the results. For example, "moshpit crowd" returned a
    /// protest rally, and "juke joint dancing" returned a ballet mannequin. "outdoor soundsystem night"
    /// returned a studio mixing desk, and "megaphone crowd" returned nothing at all. Each line here was
    /// chosen by reading what the index returned.</para>
    ///
    /// <para><b>A pair whose music belongs to a decade asks for that decade.</b> "Northern Soul" is a
    /// mid-sixties scene, and "Swing Floor" is a nineteen-forties one. A photograph of a modern club under
    /// either name would not match its title, and the covers would mix eras. So the lines that CAN carry a
    /// period do, such as "1970s disco ball", "art deco theatre interior", "seven inch singles box" and
    /// "1990s rave warehouse". The lines that cannot carry a period do not. A fireplace, a still lake and a
    /// gathering storm belong to no decade. Adding one would narrow the search for nothing.</para>
    ///
    /// <para>The period goes in as a word the index actually holds, such as "1980s", "vintage", "retro" or
    /// "art deco". It is not a year, because that is how photographers tag pictures and how the search
    /// reads them. The genre's own era decides the period. The same mood over two genres gives two decades.
    /// That is why this is a table of pairs and not four searches.</para>
    ///
    /// <para><b>Lines must not ask for a PERSON.</b> A query for people returns faces, and the covers must
    /// not show faces. Such a query is refused by every answer it gets, and the cover falls back to a drawn
    /// figure. So the lines ask for the ROOM, the INSTRUMENT, the LIGHT or the silhouette. For example: a
    /// ballroom, not ballroom dancers. A stack of vinyl, not a dancefloor. A tambourine, not a choir. A
    /// picture of the place where a kind of music happens carries that music. It is also a picture this app
    /// is allowed to use.</para>
    ///
    /// <para><b>Each genre has its own line.</b> Without it, every genre row under one mood would run the
    /// same search. The rows could differ only by which of the same answers they landed on, and a chill
    /// hip-hop list could show a nineteenth-century British seascape.</para>
    ///
    /// <para>There is no line for <see cref="Metadata.Genres.Unknown"/>. It is the answer for a record that
    /// matched no other genre. The shelf never breaks it out, and no scene means "we could not
    /// tell".</para>
    /// </summary>
    private static readonly Dictionary<(SmartPlaylistKind Kind, string Genre), string> Within = new()
    {
        // ── Chill: the quiet end of each kind of music ──
        [(SmartPlaylistKind.Calm, "Rock")] = "embers dusk",
        [(SmartPlaylistKind.Calm, "Pop")] = "soft light window",
        [(SmartPlaylistKind.Calm, "Hip-Hop / Rap")] = "rain window night",
        [(SmartPlaylistKind.Calm, "Soul")] = "retro lounge interior lamp",
        [(SmartPlaylistKind.Calm, "Electronic / Dance")] = "drifting clouds",
        [(SmartPlaylistKind.Calm, "Jazz")] = "art deco bar interior",
        [(SmartPlaylistKind.Calm, "Blues")] = "railroad tracks dusk",
        [(SmartPlaylistKind.Calm, "Country")] = "wooden porch chairs",
        [(SmartPlaylistKind.Calm, "Folk")] = "fireplace hearth",
        [(SmartPlaylistKind.Calm, "Classical")] = "still lake dawn",
        [(SmartPlaylistKind.Calm, "Metal")] = "gathering clouds grey",
        [(SmartPlaylistKind.Calm, "Punk")] = "empty street dawn",
        [(SmartPlaylistKind.Calm, "Reggae")] = "palm shade beach",
        [(SmartPlaylistKind.Calm, "Latin")] = "vintage rio beach",
        [(SmartPlaylistKind.Calm, "World")] = "distant shore horizon",
        [(SmartPlaylistKind.Calm, "Religious")] = "stained glass light",
        [(SmartPlaylistKind.Calm, "Soundtrack")] = "empty cinema seats",
        [(SmartPlaylistKind.Calm, "Spoken Word")] = "book candlelight",
        [(SmartPlaylistKind.Calm, "Indie")] = "bedroom window rain",
        [(SmartPlaylistKind.Calm, "Disco")] = "mirrorball reflections dim",
        [(SmartPlaylistKind.Calm, "Funk")] = "1970s wood panel den",
        [(SmartPlaylistKind.Calm, "R&B")] = "silk curtains lamplight",
        [(SmartPlaylistKind.Calm, "Rap")] = "city rooftop dusk",
        [(SmartPlaylistKind.Calm, "Trap")] = "empty parking lot night",
        [(SmartPlaylistKind.Calm, "Old School Hip Hop")] = "cassette tapes shelf",
        [(SmartPlaylistKind.Calm, "House")] = "sunrise over water",
        [(SmartPlaylistKind.Calm, "Techno")] = "concrete corridor grey",
        [(SmartPlaylistKind.Calm, "Club")] = "neon sign wet pavement",
        [(SmartPlaylistKind.Calm, "Neo-Classical")] = "snow field bare tree",
        [(SmartPlaylistKind.Calm, "Opera")] = "velvet theatre curtain",
        [(SmartPlaylistKind.Calm, "Noise")] = "static screen grain",
        [(SmartPlaylistKind.Calm, "Avant-Garde")] = "white gallery wall",
        [(SmartPlaylistKind.Calm, "Video Game")] = "pixel sunset horizon",
        [(SmartPlaylistKind.Calm, "Musical")] = "empty stage single chair",

        // ── Adrenaline: the loud, busy end ──
        [(SmartPlaylistKind.Energetic, "Rock")] = "stage lights crowd",
        [(SmartPlaylistKind.Energetic, "Pop")] = "confetti stage",
        [(SmartPlaylistKind.Energetic, "Hip-Hop / Rap")] = "1980s graffiti subway train",
        [(SmartPlaylistKind.Energetic, "Soul")] = "spotlight microphone",
        [(SmartPlaylistKind.Energetic, "Electronic / Dance")] = "1990s rave warehouse",
        [(SmartPlaylistKind.Energetic, "Jazz")] = "vintage trumpet brass",
        [(SmartPlaylistKind.Energetic, "Blues")] = "vintage neon bar sign",
        [(SmartPlaylistKind.Energetic, "Country")] = "bonfire night",
        [(SmartPlaylistKind.Energetic, "Folk")] = "violin barn wooden",
        [(SmartPlaylistKind.Energetic, "Classical")] = "orchestra concert hall",
        [(SmartPlaylistKind.Energetic, "Metal")] = "lightning storm sky",
        [(SmartPlaylistKind.Energetic, "Punk")] = "torn posters wall",
        [(SmartPlaylistKind.Energetic, "Reggae")] = "outdoor concert speakers",
        [(SmartPlaylistKind.Energetic, "Latin")] = "flames fire",
        [(SmartPlaylistKind.Energetic, "World")] = "carnival float lights night",
        [(SmartPlaylistKind.Energetic, "Religious")] = "raised hands congregation",
        [(SmartPlaylistKind.Energetic, "Soundtrack")] = "motion blur city night",
        [(SmartPlaylistKind.Energetic, "Spoken Word")] = "megaphone closeup",
        [(SmartPlaylistKind.Energetic, "Indie")] = "small venue lights",
        [(SmartPlaylistKind.Energetic, "Disco")] = "1970s mirrorball beams",
        [(SmartPlaylistKind.Energetic, "Funk")] = "brass section horns",
        [(SmartPlaylistKind.Energetic, "R&B")] = "silk stage drapes",
        [(SmartPlaylistKind.Energetic, "Rap")] = "stadium floodlights night",
        [(SmartPlaylistKind.Energetic, "Trap")] = "bass speaker cone closeup",
        [(SmartPlaylistKind.Energetic, "Old School Hip Hop")] = "1980s spray cans wall",
        [(SmartPlaylistKind.Energetic, "House")] = "strobe lights ceiling",
        [(SmartPlaylistKind.Energetic, "Techno")] = "industrial hall lasers",
        [(SmartPlaylistKind.Energetic, "Club")] = "laser beams smoke",
        [(SmartPlaylistKind.Energetic, "Neo-Classical")] = "piano strings closeup",
        [(SmartPlaylistKind.Energetic, "Opera")] = "gilded balcony tiers",
        [(SmartPlaylistKind.Energetic, "Noise")] = "shattered glass shards",
        [(SmartPlaylistKind.Energetic, "Avant-Garde")] = "tangled wires sculpture",
        [(SmartPlaylistKind.Energetic, "Video Game")] = "arcade cabinet glow",
        [(SmartPlaylistKind.Energetic, "Musical")] = "footlights red curtain",

        // ── Dancefloor: busy over a level that holds still ──
        [(SmartPlaylistKind.Danceable, "Rock")] = "1970s disco ball",
        [(SmartPlaylistKind.Danceable, "Pop")] = "nightclub lights crowd",
        [(SmartPlaylistKind.Danceable, "Hip-Hop / Rap")] = "1980s boombox",
        [(SmartPlaylistKind.Danceable, "Soul")] = "seven inch singles box",
        [(SmartPlaylistKind.Danceable, "Electronic / Dance")] = "1990s rave lights",
        [(SmartPlaylistKind.Danceable, "Jazz")] = "art deco theatre interior",
        [(SmartPlaylistKind.Danceable, "Blues")] = "roadhouse bar interior night",
        [(SmartPlaylistKind.Danceable, "Country")] = "cowboy boots wooden floor",
        [(SmartPlaylistKind.Danceable, "Folk")] = "barn interior string lights",
        [(SmartPlaylistKind.Danceable, "Classical")] = "empty ballroom chandelier",
        [(SmartPlaylistKind.Danceable, "Metal")] = "concert crowd surge",
        [(SmartPlaylistKind.Danceable, "Punk")] = "mosh pit concert",
        [(SmartPlaylistKind.Danceable, "Reggae")] = "street speakers party",
        [(SmartPlaylistKind.Danceable, "Latin")] = "tropical night street lights",
        [(SmartPlaylistKind.Danceable, "World")] = "festival lanterns night",
        [(SmartPlaylistKind.Danceable, "Religious")] = "tambourine closeup",
        [(SmartPlaylistKind.Danceable, "Soundtrack")] = "vintage film projector",
        [(SmartPlaylistKind.Danceable, "Spoken Word")] = "microphone stage",
        [(SmartPlaylistKind.Danceable, "Indie")] = "basement party lights",
        [(SmartPlaylistKind.Danceable, "Disco")] = "1970s glitter floor",
        [(SmartPlaylistKind.Danceable, "Funk")] = "1970s dance floor squares",
        [(SmartPlaylistKind.Danceable, "R&B")] = "smoke lounge low light",
        [(SmartPlaylistKind.Danceable, "Rap")] = "1990s turntable closeup",
        [(SmartPlaylistKind.Danceable, "Trap")] = "subwoofer stack dark",
        [(SmartPlaylistKind.Danceable, "Old School Hip Hop")] = "linoleum floor cardboard",
        [(SmartPlaylistKind.Danceable, "House")] = "1990s dance floor smoke",
        [(SmartPlaylistKind.Danceable, "Techno")] = "strobe tunnel dark",
        [(SmartPlaylistKind.Danceable, "Club")] = "velvet rope entrance",
        [(SmartPlaylistKind.Danceable, "Neo-Classical")] = "marble hall reflections",
        [(SmartPlaylistKind.Danceable, "Opera")] = "grand foyer staircase",
        [(SmartPlaylistKind.Danceable, "Noise")] = "amplifier stack wall",
        [(SmartPlaylistKind.Danceable, "Avant-Garde")] = "geometric floor pattern",
        [(SmartPlaylistKind.Danceable, "Video Game")] = "neon grid horizon",
        [(SmartPlaylistKind.Danceable, "Musical")] = "tap shoes wooden stage",

        // ── Focus: the same still level with little happening over it ──
        //
        // These lines describe where you would be, not what the record does. The names lean the same
        // way: the hour, the room, the window. A genre with no line here uses the mood's own words.
        [(SmartPlaylistKind.Focus, "Rock")] = "golden hour sunlight window",
        [(SmartPlaylistKind.Focus, "Pop")] = "hotel lounge armchair soft light",
        [(SmartPlaylistKind.Focus, "Funk")] = "turntable vinyl warm light",
        [(SmartPlaylistKind.Focus, "Soul")] = "city window night blue",
        [(SmartPlaylistKind.Focus, "Hip-Hop / Rap")] = "city rooftop skyline dusk",
        [(SmartPlaylistKind.Focus, "Electronic / Dance")] = "office desk window daylight",
        [(SmartPlaylistKind.Focus, "Jazz")] = "piano bar interior dim light",
        [(SmartPlaylistKind.Focus, "Folk")] = "forest path morning light",

        // Lights out has no genre rows at all. It is the one mood cut absolutely rather than within a genre
        // (see SmartPlaylists.BrokenOutByGenre), so "sleep jazz" is never asked. Nothing here answers it,
        // and it uses the mood's own words.
    };
}
