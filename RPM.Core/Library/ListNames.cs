using RPM.Core.Metadata;

namespace RPM.Core.Library;

/// <summary>
/// What a generated list is called, and where it sits in the tree of lists.
///
/// <para><b>Shared by every head that names a list.</b> The desktop shows its lists and publishes them, and the
/// store builds lists for an account that has no desktop. Both name a list here, so the same list reads the same
/// on every screen, whoever cut it. The words that are interface text come from the caller's own string table,
/// passed in as <c>text</c> (a string key in, the text in the caller's language out): the desktop passes its
/// resource table, the store the table of the language the asking app uses. The titles of a mood over one genre
/// are names, not interface text, and are the same in every language (see <see cref="Titles"/>).</para>
///
/// <para>The switch below has no fallback arm. A kind added to the enum needs a name chosen on purpose.</para>
/// </summary>
public static class ListNames
{
    /// <summary>
    /// What one generated list is called.
    ///
    /// <para>A mood row uses the caller's language. A genre row under a mood does not. It has its own title from
    /// <see cref="Titles"/>, in English in every language. See that table for why.</para>
    /// </summary>
    public static string Name(SmartPlaylistId id, Func<string, string> text) =>
        // A decade is named by its decade number. This is checked first, because the fall-through below
        // ends at KindName, which has no arm for a list whose whole name is that number.
        id.IsWithinDecade ? DecadeName(id.Decade)
        : id.Genre is { } genre && Titles.TryGetValue((id.Kind, genre), out var title) ? title

        // A list that a GENRE page offers is named by the question it asks, not by the genre. Four cards
        // in a row under "Essential rock playlists" would otherwise all be called "Rock". That says
        // nothing, and says it four times. The heading above them already names the genre. See
        // SmartPlaylists.EssentialForAGenre, the set that page draws from.
        : id.Genre is not null && SmartPlaylists.EssentialForAGenre.Contains(id.Kind) ? KindName(id.Kind, text)

        // A MOOD over a genre with no title written for it falls back to the genre name. That is correct
        // here. These rows sit nested under the mood's own row in the tree, so the parent already says
        // which mood it is, and the child only says which shelf.
        : id.Genre ?? KindName(id.Kind, text);

    /// <summary>
    /// The same name, given the kind as a string. A cut names its lists by the string form of
    /// <see cref="SmartPlaylistKind"/>, not by the enum value.
    ///
    /// <para>A kind this build does not know (the other end is a newer version) falls back to that string.
    /// The list is not dropped, because a list with an unfamiliar name still plays.</para>
    /// </summary>
    public static string Name(string kind, string? genre, int decade, Func<string, string> text)
        => Enum.TryParse<SmartPlaylistKind>(kind, ignoreCase: false, out var parsed)
            ? Name(new SmartPlaylistId(parsed, genre, decade), text)
            : kind;

    /// <summary>
    /// Where a generated list sits in the tree, outermost first.
    ///
    /// <para><b>This is the one place that answers it, for every head that shows these lists.</b> The
    /// desktop's pane builds the same three folders (see <c>PlaylistModel</c>). If each head worked out the
    /// arrangement at its own end, they could disagree. A borrowed shelf could then show the eras and the mood
    /// styles flat in one folder, and a phone could show its own shape. A cut is a shelf as its owner arranged
    /// it. The arrangement travels with the cut, and this method gives the answer that is sent.</para>
    ///
    /// <para>A mood narrowed to a genre hangs under its own mood's row. A decade is a sibling of the moods, not
    /// one of them. Everything that is neither (the lists about listening rather than about sound) is in the
    /// pinned folder.</para>
    /// </summary>
    public static IReadOnlyList<string> FolderChain(string kind, string? genre, int decade, Func<string, string> text)
    {
        if (decade > 0)
            return [DecadesFolderName(text)];

        if (!Enum.TryParse<SmartPlaylistKind>(kind, ignoreCase: false, out var parsed))
            return [MoodsFolderName(text)];

        if (!SmartPlaylists.Moods.Contains(parsed))
            return [FolderName(text)];

        return genre is { Length: > 0 }
            ? [MoodsFolderName(text), Name(SmartPlaylistId.WholeShelf(parsed), text)]
            : [MoodsFolderName(text)];
    }

    /// <summary>The pinned folder's label. It is the one row in the tree that says what everything under it
    /// is.</summary>
    public static string FolderName(Func<string, string> text) => text("Playlists_SmartFolder");

    /// <summary>The decades folder's name, a sibling of <see cref="FolderName"/>.</summary>
    public static string DecadesFolderName(Func<string, string> text) => text("Playlists_DecadesFolder");

    /// <summary>The moods folder's name, a sibling of <see cref="FolderName"/> and of
    /// <see cref="DecadesFolderName"/>.</summary>
    public static string MoodsFolderName(Func<string, string> text) => text("Playlists_MoodsFolder");

    /// <summary>What one decade's folder is called. It is not localized and not a resource. "1970s" is a
    /// number, and reads the same in every language the app speaks.</summary>
    public static string DecadeName(int decade) => $"{decade}s";

    /// <summary>
    /// The title a mood uses over one genre. For example, "Slow Burn" rather than "Chill / Rock".
    ///
    /// <para>Each pair is written out by hand. The combined form read as a mood above a category. "Chill"
    /// above "Electronic / Dance" is a mood above a taxonomy, and the second half does the job of a shelf
    /// label, not a playlist name. A formula would be less typing, but no formula can produce "Four to the
    /// Floor" from a genre string.</para>
    ///
    /// <para>The titles are in English in every locale, on purpose. They are names, not interface text. A
    /// song's name is not translated either. A name translated into five languages would be five weaker
    /// names. The genres wall already shows <see cref="Genres.All"/> untranslated, so these titles match
    /// it. The titles are kept here, not in <c>strings.json</c>, because that file holds interface text
    /// only.</para>
    ///
    /// <para>Some titles follow their records. Some genres were split out of broader shelves, and their
    /// records moved with them. So "Funk Fever" is the loud end of Funk, where the funk records now are.
    /// "Soul Jams", "House Party" and "Rap Core" follow the soul, house and rap records. A square that a
    /// title has left has no line here. See MoodSets.Absorbed.</para>
    ///
    /// <para>Half the answer. <see cref="MoodSets"/> is the other half. This table says what a list is
    /// called. MoodSets says which squares of the grid each list draws from, and which squares are no
    /// longer lists at all because another list took them over. A square that MoodSets marks as taken
    /// over has no line here. If it had one, the name could still be reached through a shared cut, and it
    /// would read as a list this app does not have. A test checks the two tables against each
    /// other.</para>
    ///
    /// <para>The titles are in the order of <see cref="Genres.All"/>, so this table can be read down
    /// against the list it answers. Every genre has a line under every mood, except the squares that
    /// MoodSets has taken over or dropped. A test checks this, and checks that no two titles are the same.</para>
    ///
    /// <para>A pair with no title falls back to the genre name. A genre added to <see cref="Genres.All"/>
    /// later reads as itself, not as nothing.</para>
    /// </summary>
    private static readonly Dictionary<(SmartPlaylistKind Kind, string Genre), string> Titles = new()
    {
        // Chill: the quiet end of each kind of music
        [(SmartPlaylistKind.Calm, "Rock")] = "Slow Burn",
        [(SmartPlaylistKind.Calm, "Indie")] = "Bedroom Tapes",
        [(SmartPlaylistKind.Calm, "Pop")] = "Soft Pop",
        [(SmartPlaylistKind.Calm, "Disco")] = "After the Party",
        [(SmartPlaylistKind.Calm, "Funk")] = "Soft Groove",
        [(SmartPlaylistKind.Calm, "Soul")] = "Late Night Soul",   // was "Quiet Storm", now Date Night's
        [(SmartPlaylistKind.Calm, "R&B")] = "Soft Velvet",
        [(SmartPlaylistKind.Calm, "Rap")] = "Low End",
        [(SmartPlaylistKind.Calm, "Trap")] = "Night Drive",
        [(SmartPlaylistKind.Calm, "Old School Hip Hop")] = "Corner Store",
        [(SmartPlaylistKind.Calm, "Hip-Hop / Rap")] = "Just the Bars",
        // No line for (Calm, "House"), once "Sunrise Set": that square was dropped (see MoodSets.Dropped).
        [(SmartPlaylistKind.Calm, "Techno")] = "Slow Machine",
        [(SmartPlaylistKind.Calm, "Club")] = "Closing Time",
        [(SmartPlaylistKind.Calm, "Electronic / Dance")] = "Ambient Drift",
        [(SmartPlaylistKind.Calm, "Jazz")] = "Jazz Club",
        [(SmartPlaylistKind.Calm, "Blues")] = "Slow Blues",
        [(SmartPlaylistKind.Calm, "Country")] = "Porch Light",
        [(SmartPlaylistKind.Calm, "Folk")] = "Hearthside",
        [(SmartPlaylistKind.Calm, "Classical")] = "Adagio",
        [(SmartPlaylistKind.Calm, "Neo-Classical")] = "Felt Piano",
        [(SmartPlaylistKind.Calm, "Opera")] = "Aria",
        [(SmartPlaylistKind.Calm, "Metal")] = "The Calm Before",
        [(SmartPlaylistKind.Calm, "Punk")] = "Comedown",
        // The last two shelves, in the order Genres.All keeps them. The fallback would hide a missing
        // title on screen, so a test checks that every genre has a title of its own.
        [(SmartPlaylistKind.Calm, "Noise")] = "Low Hum",
        [(SmartPlaylistKind.Calm, "Avant-Garde")] = "Long Tones",
        [(SmartPlaylistKind.Calm, "Reggae")] = "Island Time",
        [(SmartPlaylistKind.Calm, "Latin")] = "Bossa Hours",
        [(SmartPlaylistKind.Calm, "World")] = "Distant Shores",
        [(SmartPlaylistKind.Calm, "Religious")] = "Sanctuary",
        // No line for (Calm, "Soundtrack"), once "End Credits": dropped (see MoodSets.Dropped).
        [(SmartPlaylistKind.Calm, "Video Game")] = "Save Point",
        [(SmartPlaylistKind.Calm, "Musical")] = "Curtain Down",
        [(SmartPlaylistKind.Calm, "Spoken Word")] = "Quiet Words",

        // Adrenaline: the loud, busy end
        [(SmartPlaylistKind.Energetic, "Rock")] = "Amplified",
        // No line for (Energetic, "Indie"): that square is taken over by another list (see
        // MoodSets.Absorbed).
        [(SmartPlaylistKind.Energetic, "Pop")] = "Pop Fever",
        [(SmartPlaylistKind.Energetic, "Disco")] = "Hustle",

        // No line for (Energetic, "Funk"): that square is taken over by another list (see
        // MoodSets.Absorbed). A square that nothing offers must have no name. Otherwise the name could be
        // reached through a shared cut and read as a list this app does not have.
        [(SmartPlaylistKind.Energetic, "Soul")] = "Funkadelica",
        [(SmartPlaylistKind.Energetic, "R&B")] = "Shout",

        // Records tagged "Rap" are on this square.
        [(SmartPlaylistKind.Energetic, "Rap")] = "Rap Core",   // no other list takes this square
        [(SmartPlaylistKind.Energetic, "Trap")] = "808s",
        [(SmartPlaylistKind.Energetic, "Old School Hip Hop")] = "Boom Bap",
        [(SmartPlaylistKind.Energetic, "Hip-Hop / Rap")] = "Rapsody",

        // House records are not filed under Electronic / Dance.
        [(SmartPlaylistKind.Energetic, "House")] = "House Party",
        [(SmartPlaylistKind.Energetic, "Techno")] = "Berlin Nights",
        [(SmartPlaylistKind.Energetic, "Club")] = "Big Night",
        [(SmartPlaylistKind.Energetic, "Electronic / Dance")] = "Main Floor",
        [(SmartPlaylistKind.Energetic, "Jazz")] = "Hard Bop",
        [(SmartPlaylistKind.Energetic, "Blues")] = "Juke Joint",
        [(SmartPlaylistKind.Energetic, "Country")] = "Tailgate",
        // No line for (Energetic, "Folk"): that square is taken over by another list (see MoodSets.Absorbed).
        // No line for (Energetic, "Classical"), once "Fortissimo": dropped (see MoodSets.Dropped).
        [(SmartPlaylistKind.Energetic, "Neo-Classical")] = "Strings Rising",
        [(SmartPlaylistKind.Energetic, "Opera")] = "Grand Finale",
        [(SmartPlaylistKind.Energetic, "Metal")] = "Wall of Sound",
        [(SmartPlaylistKind.Energetic, "Punk")] = "Fast and Loud",
        [(SmartPlaylistKind.Energetic, "Noise")] = "Blown Speakers",
        [(SmartPlaylistKind.Energetic, "Avant-Garde")] = "Free Blowing",
        [(SmartPlaylistKind.Energetic, "Reggae")] = "Dancehall Heat",
        [(SmartPlaylistKind.Energetic, "Latin")] = "Fuego",
        [(SmartPlaylistKind.Energetic, "World")] = "Carnival",
        [(SmartPlaylistKind.Energetic, "Religious")] = "Hands Up",
        [(SmartPlaylistKind.Energetic, "Soundtrack")] = "Chase Scene",
        [(SmartPlaylistKind.Energetic, "Video Game")] = "Boss Fight",
        [(SmartPlaylistKind.Energetic, "Musical")] = "Show Stopper",
        [(SmartPlaylistKind.Energetic, "Spoken Word")] = "Full Voice",

        // Dancefloor: busy over a level that holds still
        [(SmartPlaylistKind.Danceable, "Rock")] = "Rock Anthems",

        // A club night. The name fits the indie records on this shelf.
        [(SmartPlaylistKind.Danceable, "Indie")] = "Indie Disco",
        [(SmartPlaylistKind.Danceable, "Pop")] = "Club Pop",
        [(SmartPlaylistKind.Danceable, "Disco")] = "Disco Fever",
        [(SmartPlaylistKind.Danceable, "Funk")] = "Get Down",

        // The soul records are on this shelf, so the name follows them.
        [(SmartPlaylistKind.Danceable, "Soul")] = "Soul Jams",
        [(SmartPlaylistKind.Danceable, "R&B")] = "Two Step",
        // No line for (Danceable, "Rap"): that square is taken over by another list (see MoodSets.Absorbed).
        [(SmartPlaylistKind.Danceable, "Trap")] = "Hi Hats",
        [(SmartPlaylistKind.Danceable, "Old School Hip Hop")] = "Cardboard Floor",
        [(SmartPlaylistKind.Danceable, "Hip-Hop / Rap")] = "Block Party",
        [(SmartPlaylistKind.Danceable, "House")] = "Warehouse",
        [(SmartPlaylistKind.Danceable, "Techno")] = "Steady Pulse",
        [(SmartPlaylistKind.Danceable, "Club")] = "Guest List",
        [(SmartPlaylistKind.Danceable, "Electronic / Dance")] = "Four to the Floor",
        [(SmartPlaylistKind.Danceable, "Jazz")] = "Swing Shift",   // was "A Moment for the Lovers", now Date Night's
        [(SmartPlaylistKind.Danceable, "Blues")] = "Shuffle",
        [(SmartPlaylistKind.Danceable, "Country")] = "Line Dance",
        [(SmartPlaylistKind.Danceable, "Folk")] = "Barn Dance",
        [(SmartPlaylistKind.Danceable, "Classical")] = "Ballroom",
        [(SmartPlaylistKind.Danceable, "Neo-Classical")] = "Minimal Motion",
        [(SmartPlaylistKind.Danceable, "Opera")] = "Overture",
        [(SmartPlaylistKind.Danceable, "Metal")] = "Pit Floor",
        [(SmartPlaylistKind.Danceable, "Punk")] = "Pogo",
        [(SmartPlaylistKind.Danceable, "Noise")] = "Machine Rhythm",
        [(SmartPlaylistKind.Danceable, "Avant-Garde")] = "Cut and Splice",
        [(SmartPlaylistKind.Danceable, "Reggae")] = "Soundsystem",
        [(SmartPlaylistKind.Danceable, "Latin")] = "Salsa Night",
        [(SmartPlaylistKind.Danceable, "World")] = "Global Floor",
        [(SmartPlaylistKind.Danceable, "Religious")] = "Clap and Stomp",
        [(SmartPlaylistKind.Danceable, "Soundtrack")] = "Title Sequence",
        [(SmartPlaylistKind.Danceable, "Video Game")] = "Bonus Stage",
        [(SmartPlaylistKind.Danceable, "Musical")] = "Big Number",
        [(SmartPlaylistKind.Danceable, "Spoken Word")] = "Spoken Groove",

        // Focus: the same still level with little happening over it
        //
        // Focus records are never the same as the Dancefloor records above. One wants busy and the other
        // sparse, so the two cuts do not meet. The names go two ways. Some say what a record is doing
        // ("Instrumentals", "Held Note", "One Piano"). Some say when you would play it ("Golden Hour",
        // "Midnight Mood"). Together they are the two things a listener chooses between when they pick
        // this block over the one above.
        [(SmartPlaylistKind.Focus, "Rock")] = "Golden Hour",
        // No line for (Focus, "Indie"): that square is taken over by another list (see MoodSets.Absorbed).
        [(SmartPlaylistKind.Focus, "Pop")] = "Pop Lounge",
        [(SmartPlaylistKind.Focus, "Disco")] = "Long Intro",
        [(SmartPlaylistKind.Focus, "Funk")] = "Slow Jamz",
        [(SmartPlaylistKind.Focus, "Soul")] = "Midnight Mood",
        [(SmartPlaylistKind.Focus, "R&B")] = "Smooth Sail",
        // No line for (Focus, "Rap"): that square is taken over by another list (see MoodSets.Absorbed).
        [(SmartPlaylistKind.Focus, "Trap")] = "Loop Tape",
        [(SmartPlaylistKind.Focus, "Old School Hip Hop")] = "Beat Tape",
        [(SmartPlaylistKind.Focus, "Hip-Hop / Rap")] = "Rap Life",
        // No line for (Focus, "House"): that square is taken over by another list (see MoodSets.Absorbed).
        [(SmartPlaylistKind.Focus, "Techno")] = "Loop Room",
        [(SmartPlaylistKind.Focus, "Club")] = "Warm Up",
        [(SmartPlaylistKind.Focus, "Electronic / Dance")] = "9-to-5",
        [(SmartPlaylistKind.Focus, "Jazz")] = "The Lounge",
        [(SmartPlaylistKind.Focus, "Blues")] = "Slow Hand",
        [(SmartPlaylistKind.Focus, "Country")] = "Open Road",
        [(SmartPlaylistKind.Focus, "Folk")] = "Folk Off",
        [(SmartPlaylistKind.Focus, "Classical")] = "Andante",
        [(SmartPlaylistKind.Focus, "Neo-Classical")] = "One Piano",
        [(SmartPlaylistKind.Focus, "Opera")] = "Recitative",
        [(SmartPlaylistKind.Focus, "Metal")] = "Drone Riff",
        [(SmartPlaylistKind.Focus, "Punk")] = "Rehearsal Room",
        [(SmartPlaylistKind.Focus, "Noise")] = "Flat Line",
        [(SmartPlaylistKind.Focus, "Avant-Garde")] = "Held Note",
        [(SmartPlaylistKind.Focus, "Reggae")] = "Easy Skank",
        [(SmartPlaylistKind.Focus, "Latin")] = "Nylon Strings",
        [(SmartPlaylistKind.Focus, "World")] = "Long Journey",
        [(SmartPlaylistKind.Focus, "Religious")] = "Vespers",
        [(SmartPlaylistKind.Focus, "Soundtrack")] = "Underscore",
        [(SmartPlaylistKind.Focus, "Video Game")] = "Overworld",
        [(SmartPlaylistKind.Focus, "Musical")] = "Between Numbers",
        [(SmartPlaylistKind.Focus, "Spoken Word")] = "Low Murmur",

        // ── What the sound model hears (2026-11-18) ──
        //
        // The titles of the rows on the library they were made for were chosen by hand, and are kept exactly
        // as written, lower case and full stops included. The rest are in the same spirit. Rap has no line under any of the
        // four: its row is drawn into Hip-Hop / Rap (MoodSets). Nor do the genres a list never holds
        // (MoodBands.NeverIn), which MoodSets drops.

        // Melancholy: the saddest end of each kind of music
        [(SmartPlaylistKind.Melancholy, "Rock")] = "Grey Skies",
        [(SmartPlaylistKind.Melancholy, "Indie")] = "The Art of Losing You",
        [(SmartPlaylistKind.Melancholy, "Pop")] = "Bittersweet",
        [(SmartPlaylistKind.Melancholy, "Disco")] = "Tears on the Dancefloor",
        [(SmartPlaylistKind.Melancholy, "Funk")] = "Slow Fade",
        [(SmartPlaylistKind.Melancholy, "Soul")] = "Rainy Day Soul",
        [(SmartPlaylistKind.Melancholy, "R&B")] = "Lonely Hours",
        [(SmartPlaylistKind.Melancholy, "Trap")] = "Numb",
        [(SmartPlaylistKind.Melancholy, "Old School Hip Hop")] = "Hard Times",
        [(SmartPlaylistKind.Melancholy, "Hip-Hop / Rap")] = "feels.",
        [(SmartPlaylistKind.Melancholy, "House")] = "Blue Hour",
        [(SmartPlaylistKind.Melancholy, "Techno")] = "Cold Light",
        [(SmartPlaylistKind.Melancholy, "Club")] = "Empty Floor",
        [(SmartPlaylistKind.Melancholy, "Electronic / Dance")] = "Neon Rain",
        [(SmartPlaylistKind.Melancholy, "Jazz")] = "Midnight Cafe",
        [(SmartPlaylistKind.Melancholy, "Blues")] = "Low Down",
        [(SmartPlaylistKind.Melancholy, "Country")] = "Whiskey Tears",
        [(SmartPlaylistKind.Melancholy, "Folk")] = "Laments",
        [(SmartPlaylistKind.Melancholy, "Classical")] = "Lacrimosa",
        [(SmartPlaylistKind.Melancholy, "Neo-Classical")] = "Autumn Keys",
        [(SmartPlaylistKind.Melancholy, "Opera")] = "Tragedia",
        [(SmartPlaylistKind.Melancholy, "Metal")] = "Doom and Gloom",
        [(SmartPlaylistKind.Melancholy, "Punk")] = "Wasted Youth",
        [(SmartPlaylistKind.Melancholy, "Noise")] = "Static",
        [(SmartPlaylistKind.Melancholy, "Avant-Garde")] = "Fading Signal",
        [(SmartPlaylistKind.Melancholy, "Reggae")] = "Rainy Season",
        [(SmartPlaylistKind.Melancholy, "Latin")] = "Saudade",
        [(SmartPlaylistKind.Melancholy, "World")] = "Far From Home",
        [(SmartPlaylistKind.Melancholy, "Religious")] = "Requiem",
        [(SmartPlaylistKind.Melancholy, "Soundtrack")] = "Bittersweet End",
        [(SmartPlaylistKind.Melancholy, "Video Game")] = "Game Over",
        [(SmartPlaylistKind.Melancholy, "Musical")] = "Empty Stage",
        [(SmartPlaylistKind.Melancholy, "Spoken Word")] = "Unsent Letters",

        // Feel Good: the happiest end of each kind of music
        [(SmartPlaylistKind.Happy, "Rock")] = "Sunshine rock",
        [(SmartPlaylistKind.Happy, "Indie")] = "Sunny Side Up",
        [(SmartPlaylistKind.Happy, "Pop")] = "Sugar Rush",
        [(SmartPlaylistKind.Happy, "Disco")] = "Good Times",
        [(SmartPlaylistKind.Happy, "Funk")] = "Easy Grooves",
        [(SmartPlaylistKind.Happy, "Soul")] = "Sunshine",
        [(SmartPlaylistKind.Happy, "R&B")] = "Golden",
        [(SmartPlaylistKind.Happy, "Trap")] = "Big Mood",
        [(SmartPlaylistKind.Happy, "Old School Hip Hop")] = "Summer Jam",
        [(SmartPlaylistKind.Happy, "Hip-Hop / Rap")] = "Joyride",
        [(SmartPlaylistKind.Happy, "House")] = "Dopamine",
        [(SmartPlaylistKind.Happy, "Techno")] = "Sunrise Set",
        [(SmartPlaylistKind.Happy, "Club")] = "Good Night Out",
        [(SmartPlaylistKind.Happy, "Electronic / Dance")] = "Euphoria",
        [(SmartPlaylistKind.Happy, "Jazz")] = "Swingin'",
        [(SmartPlaylistKind.Happy, "Blues")] = "Sunny Blues",
        [(SmartPlaylistKind.Happy, "Country")] = "Back Roads",
        [(SmartPlaylistKind.Happy, "Folk")] = "Sunday Picnic",
        [(SmartPlaylistKind.Happy, "Classical")] = "Allegro",
        [(SmartPlaylistKind.Happy, "Neo-Classical")] = "Bright Keys",
        [(SmartPlaylistKind.Happy, "Opera")] = "Brindisi",
        [(SmartPlaylistKind.Happy, "Metal")] = "Heavy Sunshine",
        [(SmartPlaylistKind.Happy, "Punk")] = "Skate Park",
        [(SmartPlaylistKind.Happy, "Noise")] = "Happy Noise",
        [(SmartPlaylistKind.Happy, "Avant-Garde")] = "Odd Joy",
        [(SmartPlaylistKind.Happy, "Reggae")] = "Sunsplash",
        [(SmartPlaylistKind.Happy, "Latin")] = "Fiesta",
        [(SmartPlaylistKind.Happy, "World")] = "Festival",
        [(SmartPlaylistKind.Happy, "Religious")] = "Hallelujah",
        [(SmartPlaylistKind.Happy, "Soundtrack")] = "Happy Ending",
        [(SmartPlaylistKind.Happy, "Video Game")] = "Level Up",
        [(SmartPlaylistKind.Happy, "Musical")] = "Jazz Hands",
        [(SmartPlaylistKind.Happy, "Spoken Word")] = "Good News",

        // Rage: the angriest end of each kind of music it holds
        [(SmartPlaylistKind.Angry, "Rock")] = "Seeing Red",
        [(SmartPlaylistKind.Angry, "Indie")] = "Burn.",
        [(SmartPlaylistKind.Angry, "Pop")] = "burning bridges",
        [(SmartPlaylistKind.Angry, "Trap")] = "Smoke",
        [(SmartPlaylistKind.Angry, "Old School Hip Hop")] = "Fight the Power",
        [(SmartPlaylistKind.Angry, "Hip-Hop / Rap")] = "No Mercy",
        [(SmartPlaylistKind.Angry, "House")] = "I Hope You Explode",
        [(SmartPlaylistKind.Angry, "Techno")] = "Hard Reset",
        [(SmartPlaylistKind.Angry, "Club")] = "Rage at the Function",
        [(SmartPlaylistKind.Angry, "Electronic / Dance")] = "Rage Mode",
        [(SmartPlaylistKind.Angry, "Jazz")] = "Blowout",
        [(SmartPlaylistKind.Angry, "Blues")] = "Mean Streak",
        [(SmartPlaylistKind.Angry, "Country")] = "Outlaw",
        [(SmartPlaylistKind.Angry, "Folk")] = "Protest Songs",
        [(SmartPlaylistKind.Angry, "Classical")] = "Dies Irae",
        [(SmartPlaylistKind.Angry, "Neo-Classical")] = "Tempest",
        [(SmartPlaylistKind.Angry, "Opera")] = "Vendetta",
        [(SmartPlaylistKind.Angry, "Metal")] = "Mosh Pit",
        [(SmartPlaylistKind.Angry, "Punk")] = "No Future",
        [(SmartPlaylistKind.Angry, "Noise")] = "Feedback",
        [(SmartPlaylistKind.Angry, "Avant-Garde")] = "Scream Therapy",
        [(SmartPlaylistKind.Angry, "Reggae")] = "Rebel Music",
        [(SmartPlaylistKind.Angry, "Latin")] = "Furia",
        [(SmartPlaylistKind.Angry, "World")] = "War Drums",
        [(SmartPlaylistKind.Angry, "Religious")] = "Wrath",
        [(SmartPlaylistKind.Angry, "Video Game")] = "Final Boss",
        [(SmartPlaylistKind.Angry, "Spoken Word")] = "Rant",

        // Date Night: the most romantic end of each kind of music it holds. Quiet Storm and A Moment for the
        // Lovers came here from Chill and Dancefloor, which took Late Night Soul and Swing Shift instead.
        [(SmartPlaylistKind.Romantic, "Rock")] = "Power Ballads",
        [(SmartPlaylistKind.Romantic, "Indie")] = "Love Stories",
        [(SmartPlaylistKind.Romantic, "Pop")] = "Crush",
        [(SmartPlaylistKind.Romantic, "Disco")] = "final call",
        [(SmartPlaylistKind.Romantic, "Funk")] = "Love Machine",
        [(SmartPlaylistKind.Romantic, "Soul")] = "Quiet Storm",
        [(SmartPlaylistKind.Romantic, "R&B")] = "Candlelight",
        [(SmartPlaylistKind.Romantic, "Trap")] = "Late Night Texts",
        [(SmartPlaylistKind.Romantic, "Old School Hip Hop")] = "Mixtape Love",
        [(SmartPlaylistKind.Romantic, "Hip-Hop / Rap")] = "Love Letters",
        [(SmartPlaylistKind.Romantic, "House")] = "Lovers' Groove",
        [(SmartPlaylistKind.Romantic, "Techno")] = "Heartbeat",
        [(SmartPlaylistKind.Romantic, "Electronic / Dance")] = "Neon Hearts",
        [(SmartPlaylistKind.Romantic, "Jazz")] = "A Moment for the Lovers",
        [(SmartPlaylistKind.Romantic, "Blues")] = "At Last",
        [(SmartPlaylistKind.Romantic, "Country")] = "Two Hearts",
        [(SmartPlaylistKind.Romantic, "Folk")] = "Sweetheart Songs",
        [(SmartPlaylistKind.Romantic, "Classical")] = "Liebestraum",
        [(SmartPlaylistKind.Romantic, "Neo-Classical")] = "Moonlit Keys",
        [(SmartPlaylistKind.Romantic, "Opera")] = "Amore",
        [(SmartPlaylistKind.Romantic, "Metal")] = "Black Roses",
        [(SmartPlaylistKind.Romantic, "Punk")] = "Teenage Kicks",
        [(SmartPlaylistKind.Romantic, "Noise")] = "Static Love",
        [(SmartPlaylistKind.Romantic, "Avant-Garde")] = "Strange Love",
        [(SmartPlaylistKind.Romantic, "Reggae")] = "Lovers Rock",
        [(SmartPlaylistKind.Romantic, "Latin")] = "Bésame",
        [(SmartPlaylistKind.Romantic, "World")] = "Serenade",
        [(SmartPlaylistKind.Romantic, "Religious")] = "Wedding Song",
        [(SmartPlaylistKind.Romantic, "Soundtrack")] = "Love Theme",
        [(SmartPlaylistKind.Romantic, "Video Game")] = "Player Two",
        [(SmartPlaylistKind.Romantic, "Musical")] = "Love Duet",
        [(SmartPlaylistKind.Romantic, "Spoken Word")] = "Love Poems",
    };

    /// <summary>The label of a list that covers a whole shelf, in the caller's language. It is not stored
    /// anywhere, because a generated list is not something a person names.</summary>
    private static string KindName(SmartPlaylistKind kind, Func<string, string> text) => kind switch
    {
        SmartPlaylistKind.FavoriteTracks => text("Playlists_FavoriteTracks"),
        SmartPlaylistKind.MostPlayed => text("Playlists_MostPlayed"),
        SmartPlaylistKind.RecentlyAdded => text("Playlists_RecentlyAdded"),
        SmartPlaylistKind.RecentlyPlayed => text("Playlists_RecentlyPlayed"),
        SmartPlaylistKind.NeverPlayed => text("Playlists_NeverPlayed"),
        SmartPlaylistKind.LovedAndForgotten => text("Playlists_LovedAndForgotten"),
        SmartPlaylistKind.PlayedNotLoved => text("Playlists_PlayedNotLoved"),
        SmartPlaylistKind.OnRepeat => text("Playlists_OnRepeat"),
        SmartPlaylistKind.Rediscover => text("Playlists_Rediscover"),
        SmartPlaylistKind.LateNight => text("Playlists_LateNight"),
        SmartPlaylistKind.FirstListens => text("Playlists_FirstListens"),
        SmartPlaylistKind.KeepSkipping => text("Playlists_KeepSkipping"),
        SmartPlaylistKind.Calm => text("Playlists_Calm"),
        SmartPlaylistKind.Energetic => text("Playlists_Energetic"),
        SmartPlaylistKind.Danceable => text("Playlists_Danceable"),
        SmartPlaylistKind.Focus => text("Playlists_Focus"),
        SmartPlaylistKind.Sleep => text("Playlists_Sleep"),
        SmartPlaylistKind.Melancholy => text("Playlists_Melancholy"),
        SmartPlaylistKind.Happy => text("Playlists_Happy"),
        SmartPlaylistKind.Angry => text("Playlists_Angry"),
        SmartPlaylistKind.Romantic => text("Playlists_Romantic"),
        SmartPlaylistKind.Zen => text("Playlists_Zen"),
        SmartPlaylistKind.NewYearsEve => text("Playlists_NewYearsEve"),
        SmartPlaylistKind.Christmas => text("Playlists_Christmas"),
        SmartPlaylistKind.RightNow => text("Playlists_RightNow"),

        // Name(SmartPlaylistId) answers a decade by its number first, so this arm is not reached through
        // it. It is written anyway: a missing arm is what the throw at the bottom of this switch is for.
        SmartPlaylistKind.Decade => text("Playlists_DecadesFolder"),
        SmartPlaylistKind.Classical => text("Playlists_Classical"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "This generated playlist has no name yet."),
    };
}
