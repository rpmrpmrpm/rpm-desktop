using RPM.Core.Library;

namespace RPM.Core.Tests;

public class ArtistWallTests
{
    private static ArtistCreditRow Row(string folder, string? albumArtist, string? artist, int tracks = 1)
        => new(folder, albumArtist, artist, tracks);

    private static ArtistWallItem Find(IReadOnlyList<ArtistWallItem> wall, string name)
        => wall.Single(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void An_ordinary_album_is_one_artist_with_one_record()
    {
        var wall = ArtistWall.Build([Row("ABBA/Arrival", "ABBA", "ABBA", 10)]);

        var abba = Assert.Single(wall);
        Assert.Equal("ABBA", abba.Name);
        Assert.Equal(["ABBA/Arrival"], abba.Albums);
        Assert.Empty(abba.Together);   // one act made it, so it is not a collaboration
        Assert.Empty(abba.Appearances);
        Assert.Equal(["ABBA/Arrival"], abba.Own);
        // Ten tracks, not twenty: the row names them as the record's artist and as its tracks' artist,
        // and it is the same ten songs either way.
        Assert.Equal(10, abba.Tracks);
    }

    /// <summary>The main purpose of the view: a guest gets a box of their own, and the record they
    /// guested on is listed under both of them.</summary>
    [Fact]
    public void A_guest_with_no_records_of_their_own_gets_no_box()
    {
        var wall = ArtistWall.Build([
            Row("Calvin Harris/18 Months", "Calvin Harris", "Calvin Harris", 12),
            Row("Calvin Harris/18 Months", "Calvin Harris", "Calvin Harris feat. Ellie Goulding", 1),
        ]);

        // His record, and only his box: she is on one track of it and has nothing of her own here, so a
        // box for her would be a page about somebody else's album.
        var harris = Assert.Single(wall);
        Assert.Equal("Calvin Harris", harris.Name);
        Assert.Equal(["Calvin Harris/18 Months"], harris.Albums);
    }

    /// <summary>And as soon as she has a record of her own, both are on the wall: hers on her shelf,
    /// his on his, with the guest appearance where the page separates it.</summary>
    [Fact]
    public void A_guest_who_also_has_a_record_of_their_own_gets_both()
    {
        var wall = ArtistWall.Build([
            Row("Calvin Harris/18 Months", "Calvin Harris", "Calvin Harris", 12),
            Row("Calvin Harris/18 Months", "Calvin Harris", "Calvin Harris feat. Ellie Goulding", 1),
            Row("Ellie Goulding/Halcyon", "Ellie Goulding", "Ellie Goulding", 13),
        ]);

        var goulding = Find(wall, "Ellie Goulding");

        Assert.Equal(["Ellie Goulding/Halcyon"], goulding.Albums);
        Assert.Equal(["Calvin Harris/18 Months"], goulding.Appearances);
        Assert.Equal(14, goulding.Tracks);
    }

    /// <summary>One person written two ways is one person. The credit as typed and the guest spelling
    /// of it are the same name to <see cref="RPM.Fingerprint.Catalog.NameMatch"/>.</summary>
    [Fact]
    public void The_same_artist_credited_two_ways_is_one_box()
    {
        var wall = ArtistWall.Build([
            Row("Ellie Goulding/Halcyon", "Ellie Goulding", "Ellie Goulding", 13),
            Row("Various/Party", "Various Artists", "Ellie Goulding", 1),
            Row("Various/Party", "Various Artists", "Calvin Harris ft. Ellie Goulding", 1),
        ]);

        // Two spellings of her on the compilation and her own record beside it: one box, and every
        // track that names her under either spelling counted once.
        Assert.Equal(15, Find(wall, "Ellie Goulding").Tracks);
        Assert.Equal(["Various/Party"], Find(wall, "Ellie Goulding").Appearances);
    }

    /// <summary>A band whose name has a separator in it is not two people, and what shows this is that
    /// the collection files a record under the name.</summary>
    [Theory]
    [InlineData("Earth, Wind & Fire")]
    [InlineData("AC/DC")]
    [InlineData("Simon & Garfunkel")]
    public void A_band_the_collection_files_a_record_under_is_never_split(string band)
    {
        var wall = ArtistWall.Build([Row("Band/Record", band, band, 9)]);

        var only = Assert.Single(wall);
        Assert.Equal(band, only.Name);
    }

    /// <summary>And the same separator between two acts the collection knows apart still splits.</summary>
    [Fact]
    public void An_ampersand_between_two_acts_still_splits()
    {
        var wall = ArtistWall.Build([
            Row("Queen/Hot Space", "Queen", "Queen", 11),
            Row("Queen/Hot Space", "Queen", "Queen & David Bowie", 1),
            // His own record, so the split has somebody to be seen on: an act with nothing of their
            // own is still named and still matched, but has no box (see the class note).
            Row("David Bowie/Low", "David Bowie", "David Bowie", 11),
        ]);

        Assert.Equal(["Queen/Hot Space"], Find(wall, "David Bowie").Appearances);
        Assert.Equal(["David Bowie/Low"], Find(wall, "David Bowie").Albums);
    }

    // -- a record two acts made together --

    /// <summary>
    /// The case the collaboration rule exists for. "Kanye West &amp; Ty Dolla $ign" is an album-artist
    /// credit like any other, so the band rule alone would keep it as one name: the record would be on
    /// neither artist's page and the wall would get a box for a partnership. Both halves are acts this
    /// collection already knows, so the credit names two people and the record is both of theirs.
    /// </summary>
    [Fact]
    public void A_record_two_known_acts_made_together_belongs_to_both_of_them()
    {
        var wall = ArtistWall.Build([
            Row("Kanye West/Graduation", "Kanye West", "Kanye West", 13),
            Row("Various/Party", "Various Artists", "Ty Dolla $ign", 1),
            Row("Kanye West & Ty Dolla $ign/Vultures 1", "Kanye West & Ty Dolla $ign",
                "Kanye West & Ty Dolla $ign", 16),
        ]);

        // Their own record stays on their own shelf; the joint one is on the shelf that says so, and
        // is on it for BOTH of them.
        Assert.Equal(["Kanye West/Graduation"], Find(wall, "Kanye West").Albums);
        Assert.Equal(["Kanye West & Ty Dolla $ign/Vultures 1"], Find(wall, "Kanye West").Together);
        Assert.Empty(Find(wall, "Ty Dolla $ign").Albums);
        Assert.Equal(["Kanye West & Ty Dolla $ign/Vultures 1"], Find(wall, "Ty Dolla $ign").Together);

        // And no box for the pairing itself. The compilation the second row is on has one, because a
        // record filed under the marker is the compilations shelf's.
        Assert.Equal(["Kanye West", "Ty Dolla $ign", ArtistWall.Compilations],
                     wall.Select(a => a.Name));
    }

    /// <summary>The joining words too, which no ordinary split touches: a credit is far more often one
    /// act with a long name than two joined by a word, so these are only ever read this way once both
    /// halves have turned out to be acts the collection knows.</summary>
    [Theory]
    [InlineData("Kanye West with Common")]
    [InlineData("Kanye West and Common")]
    [InlineData("Kanye West x Common")]
    [InlineData("Kanye West & Common")]
    [InlineData("Kanye West, Common")]
    public void A_collaboration_joined_by_a_word_is_still_two_acts(string credit)
    {
        var wall = ArtistWall.Build([
            Row("Kanye West/Graduation", "Kanye West", "Kanye West", 13),
            Row("Common/Be", "Common", "Common", 11),
            Row("Both/Record", credit, credit, 9),
        ]);

        Assert.Equal(["Kanye West", "Common"], wall.Select(a => a.Name).OrderByDescending(n => n));
        Assert.Contains("Both/Record", Find(wall, "Kanye West").Together);
        Assert.Contains("Both/Record", Find(wall, "Common").Together);
    }

    /// <summary>
    /// And the other half of the rule, which is what keeps it from taking bands apart. A collection with
    /// Nick Cave's solo records in it knows "Nick Cave" on his own, so a rule that split on ONE known
    /// part would break up the band on the strength of it. "The Bad Seeds" is nobody's own credit here,
    /// so the credit stands as the name it is.
    /// </summary>
    [Fact]
    public void A_band_is_not_split_because_one_half_of_it_is_also_a_solo_act()
    {
        var wall = ArtistWall.Build([
            Row("Nick Cave/Solo", "Nick Cave", "Nick Cave", 10),
            Row("Nick Cave & The Bad Seeds/Let Love In", "Nick Cave & The Bad Seeds",
                "Nick Cave & The Bad Seeds", 10),
        ]);

        Assert.Equal(["Nick Cave", "Nick Cave & The Bad Seeds"], wall.Select(a => a.Name));
    }

    /// <summary>A band whose name reads like a sentence is safe for the same reason: neither word in it
    /// is an act the collection has ever heard of.</summary>
    [Fact]
    public void A_band_whose_name_holds_a_joining_word_is_left_alone()
    {
        var wall = ArtistWall.Build([
            Row("Sleeping With Sirens/Feel", "Sleeping With Sirens", "Sleeping With Sirens", 12),
        ]);

        Assert.Equal("Sleeping With Sirens", Assert.Single(wall).Name);
    }

    /// <summary>A collaborator the collection has nothing else by keeps the joint box. The honest
    /// answer: the collection does not know that name as an act of its own, and a box for it would be a
    /// box nothing else on the wall agrees with.</summary>
    [Fact]
    public void A_credit_whose_other_half_is_a_stranger_stands_whole()
    {
        var wall = ArtistWall.Build([
            Row("Kanye West/Graduation", "Kanye West", "Kanye West", 13),
            Row("Kanye West & Nobody Else/Record", "Kanye West & Nobody Else",
                "Kanye West & Nobody Else", 9),
        ]);

        Assert.Equal(["Kanye West", "Kanye West & Nobody Else"], wall.Select(a => a.Name));
    }

    /// <summary>A guest credit is how a collection usually knows a collaborator, so it counts: Kid Cudi
    /// is featured on somebody's track and that is enough to make him an act the pairing can be read
    /// against.</summary>
    [Fact]
    public void A_featured_credit_is_enough_to_know_somebody_by()
    {
        var wall = ArtistWall.Build([
            Row("Kanye West/Graduation", "Kanye West", "Kanye West", 12),
            Row("Kanye West/Graduation", "Kanye West", "Kanye West feat. Kid Cudi", 1),
            Row("Kanye West & Kid Cudi/Kids See Ghosts", "Kanye West & Kid Cudi",
                "Kanye West & Kid Cudi", 7),
        ]);

        Assert.Equal(["Kanye West & Kid Cudi/Kids See Ghosts"], Find(wall, "Kid Cudi").Together);
        Assert.Equal(["Kanye West/Graduation"], Find(wall, "Kid Cudi").Appearances);
    }

    /// <summary>Their page picks their own songs off a joint record, which is the other half of what
    /// the wall's split is for.</summary>
    [Theory]
    [InlineData("Kanye West & Ty Dolla $ign", "Ty Dolla $ign")]
    [InlineData("Kanye West with Common", "Common")]
    [InlineData("Kanye West with Common", "Kanye West")]
    public void A_joint_credit_names_each_of_its_acts(string credit, string artist)
        => Assert.True(ArtistWall.Names(credit, artist));

    /// <summary>A record filed under somebody is theirs even when its tracks credit the guest lineup
    /// and never the name on the sleeve.</summary>
    [Fact]
    public void The_record_is_filed_under_its_album_artist_whatever_its_tracks_say()
    {
        var wall = ArtistWall.Build([
            Row("Santana/Supernatural", "Santana", "Santana feat. Rob Thomas", 1),
            Row("Santana/Supernatural", "Santana", "Santana feat. Everlast", 1),
        ]);

        // One box, and it is the name on the sleeve: the guests are named on every track and have
        // nothing of their own, so the record is not carried off to their pages.
        var santana = Assert.Single(wall);
        Assert.Equal("Santana", santana.Name);
        Assert.Equal(["Santana/Supernatural"], santana.Albums);
        Assert.Equal(2, santana.Tracks);
    }

    /// <summary>An appearance that turns out to be their own record is promoted, whichever order the
    /// rows arrive in: the mirror groups by folder and says nothing about which credit it reads
    /// first.</summary>
    [Fact]
    public void A_guest_credit_read_before_the_album_artist_still_leaves_one_record()
    {
        var wall = ArtistWall.Build([
            Row("Eminem/The Eminem Show", "Various Artists", "Eminem feat. Dr. Dre", 1),
            Row("Eminem/The Eminem Show", "Eminem", "Eminem", 19),
        ]);

        var eminem = Find(wall, "Eminem");
        Assert.Equal(["Eminem/The Eminem Show"], eminem.Albums);
        Assert.Empty(eminem.Appearances);
        Assert.Equal(20, eminem.Tracks);
    }

    /// <summary>The compilations are an act of their own: the marker names nobody, so the records
    /// filed under it would otherwise be on no page at all.</summary>
    [Fact]
    public void The_compilation_marker_is_the_compilations_shelf()
    {
        var wall = ArtistWall.Build([
            Row("Various/Now 40", "Various Artists", "Various", 1),
            Row("Various/Now 40", "Various Artists", "Blur", 1),
        ]);

        // One box: the compilation itself. Blur are on one track of somebody else's record and have
        // nothing of their own here.
        var only = Assert.Single(wall);
        Assert.Equal(ArtistWall.Compilations, only.Name);
        Assert.Equal(["Various/Now 40"], only.Albums);
    }

    /// <summary>However many ways the collection spells it, and it is never split or mistaken for a
    /// band.</summary>
    [Theory]
    [InlineData("Various")]
    [InlineData("Various Artists")]
    [InlineData("various artist")]
    public void Every_spelling_of_the_marker_is_the_one_shelf(string spelling)
    {
        var wall = ArtistWall.Build([
            Row("Various/Now 40", "Various Artists", "Blur", 1),
            Row("Various/Now 41", spelling, "Pulp", 1),
        ]);

        var only = Assert.Single(wall);
        Assert.Equal(ArtistWall.Compilations, only.Name);
        Assert.Equal(["Various/Now 40", "Various/Now 41"], only.Albums);
    }

    /// <summary>An artist on a compilation who has records of their own keeps them apart from it: the
    /// compilation is an appearance, not something they made.</summary>
    [Fact]
    public void A_compilation_is_an_appearance_for_the_artists_on_it()
    {
        var wall = ArtistWall.Build([
            Row("Blur/Parklife", "Blur", "Blur", 16),
            Row("Various/Now 40", "Various Artists", "Blur", 1),
        ]);

        var blur = Find(wall, "Blur");
        Assert.Equal(["Blur/Parklife"], blur.Albums);
        Assert.Equal(["Various/Now 40"], blur.Appearances);
        Assert.Equal(["Various/Now 40"], Find(wall, ArtistWall.Compilations).Albums);
    }

    [Fact]
    public void An_untagged_folder_names_nobody()
    {
        Assert.Empty(ArtistWall.Build([Row("Unsorted", null, null, 4)]));
    }

    [Fact]
    public void The_wall_reads_in_name_order()
    {
        var wall = ArtistWall.Build([
            Row("c/x", "Zappa", "Zappa"),
            Row("a/x", "Abba", "Abba"),
            Row("b/x", "middle", "middle"),
        ]);

        Assert.Equal(["Abba", "middle", "Zappa"], wall.Select(a => a.Name));
    }

    [Theory]
    [InlineData("ABBA", 'A')]
    [InlineData("the Beatles", 'T')]
    [InlineData("Sigur Rós", 'S')]
    [InlineData("Ólafur Arnalds", 'O')]
    [InlineData("!!!", '#')]
    [InlineData("2Pac", '#')]
    [InlineData("", '#')]
    [InlineData(null, '#')]
    public void Names_file_under_the_rails_letters(string? name, char expected)
        => Assert.Equal(expected, ArtistWall.Initial(name));

    /// <summary>The wall reads in the rail's order, and the rail puts its bucket last. A plain sort
    /// would put the numbers at the top (every culture sorts digits in front of letters), and the rail
    /// would then be pointing down the page at something above it.</summary>
    [Fact]
    public void Names_starting_with_neither_a_letter_read_last()
    {
        var wall = ArtistWall.Build([
            Row("num/x", "2Pac", "2Pac"),
            Row("m/x", "Madonna", "Madonna"),
            Row("sym/x", "!!!", "!!!"),
            Row("a/x", "ABBA", "ABBA"),
        ]);

        Assert.Equal(["ABBA", "Madonna", "!!!", "2Pac"], wall.Select(a => a.Name));
    }

    /// <summary>The letter comes before the name: "(hed) p.e." files under H, so it reads among the H
    /// names and not ahead of every A, where the rail would be wrong about it.</summary>
    [Fact]
    public void A_name_led_by_punctuation_reads_among_its_own_letter()
    {
        var wall = ArtistWall.Build([
            Row("h/a", "(hed) p.e.", "(hed) p.e."),
            Row("a/a", "Air", "Air"),
            Row("h/b", "Haim", "Haim"),
            Row("x/a", "!!!", "!!!"),
        ]);

        Assert.Equal(["Air", "(hed) p.e.", "Haim", "!!!"], wall.Select(a => a.Name));
    }

    /// <summary>What somebody types when they cannot remember how a band punctuates itself.</summary>
    [Theory]
    [InlineData("Sigur Rós", "sigur ros")]
    [InlineData("AC/DC", "acdc")]
    [InlineData("Guns N' Roses", "guns n roses")]
    [InlineData("The Beatles", "beatles")]
    [InlineData("Beyoncé", "BEYONCE")]
    [InlineData("Stevie Wonder", "wonder")]
    public void The_search_looks_past_case_accents_and_punctuation(string name, string typed)
        => Assert.True(ArtistWall.Matches(name, typed));

    [Theory]
    [InlineData("Stevie Wonder", "stevie nicks")]
    [InlineData("ABBA", "zappa")]
    public void And_still_says_no_to_somebody_else(string name, string typed)
        => Assert.False(ArtistWall.Matches(name, typed));

    /// <summary>A field with one bracket in it must not empty the wall while the rest of the word is
    /// still being typed.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("(")]
    [InlineData(null)]
    public void A_query_that_says_nothing_matches_everybody(string? typed)
        => Assert.True(ArtistWall.Matches("ABBA", typed));

    [Fact]
    public void The_rail_is_twenty_six_letters_and_a_bucket()
    {
        Assert.Equal(27, ArtistWall.Alphabet.Count);
        Assert.Equal('A', ArtistWall.Alphabet[0]);
        Assert.Equal('Z', ArtistWall.Alphabet[25]);
        Assert.Equal('#', ArtistWall.Alphabet[26]);
    }

    /// <summary>
    /// A name with a comma in it, in a collection that also has him as a guest. From a real library:
    /// without the rejoin, 85 tracks filed under "Tyler, The Creator" go to a box for "Tyler" and a box
    /// for "The Creator", and neither is him.
    ///
    /// <para>The album-artist rule is not the problem: his credit is in it. The trap is the
    /// collaboration rule, which reads the names a collection knows from its SPLIT track credits: the
    /// guest spots below cut him in two, both halves go into that set, and his own credit then passes
    /// the "is every part somebody we know alone?" test about himself.</para>
    /// </summary>
    [Fact]
    public void A_name_with_a_comma_in_it_survives_being_a_guest_on_somebody_elses_record()
    {
        var wall = ArtistWall.Build([
            Row("Tyler, The Creator/IGOR", "Tyler, The Creator", "Tyler, The Creator", 12),
            Row("A$AP Rocky/Testing", "A$AP Rocky", "A$AP Rocky", 15),
            Row("A$AP Rocky/Testing", "A$AP Rocky", "A$AP Rocky; Tyler, The Creator; Jozzy", 1),
            Row("The Weeknd/Hurry Up Tomorrow", "The Weeknd", "The Weeknd", 22),
            Row("The Weeknd/Hurry Up Tomorrow", "The Weeknd", "The Weeknd, Tyler, The Creator", 1),
        ]);

        Assert.Equal(["A$AP Rocky", "The Weeknd", "Tyler, The Creator"],
                     wall.Select(a => a.Name).OrderBy(n => n, StringComparer.Ordinal));

        var tyler = Find(wall, "Tyler, The Creator");
        Assert.Equal(["Tyler, The Creator/IGOR"], tyler.Albums);
        Assert.Empty(tyler.Together);   // a guest verse is not a record the two of them made
        Assert.Equal(["A$AP Rocky/Testing", "The Weeknd/Hurry Up Tomorrow"], tyler.Appearances);
    }

    /// <summary>The same rejoin, for a band rather than a person: the guest credit is cut into three and
    /// the three are put back because the collection files a record under exactly that name.</summary>
    [Fact]
    public void A_band_named_with_separators_is_rejoined_inside_a_longer_credit()
    {
        var wall = ArtistWall.Build([
            Row("Earth, Wind & Fire/I Am", "Earth, Wind & Fire", "Earth, Wind & Fire", 9),
            Row("Diana Ross/Diana", "Diana Ross", "Diana Ross", 10),
            Row("Diana Ross/Diana", "Diana Ross", "Diana Ross, Earth, Wind & Fire", 1),
        ]);

        Assert.Equal(["Diana Ross", "Earth, Wind & Fire"],
                     wall.Select(a => a.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(["Diana Ross/Diana"], Find(wall, "Earth, Wind & Fire").Appearances);
    }

    /// <summary>And nothing is rejoined that the collection does not file a record under. Without his
    /// own record in the library, the credit is still cut where the separators are: the wall has no
    /// evidence that "Tyler, The Creator" is one act, and inventing it would be a guess.</summary>
    [Fact]
    public void A_credit_the_collection_files_nothing_under_is_still_split()
    {
        var wall = ArtistWall.Build([
            Row("A$AP Rocky/Testing", "A$AP Rocky", "A$AP Rocky", 15),
            Row("A$AP Rocky/Testing", "A$AP Rocky", "A$AP Rocky; Tyler, The Creator", 1),
        ]);

        Assert.Equal(["A$AP Rocky"], wall.Select(a => a.Name));
    }
}
