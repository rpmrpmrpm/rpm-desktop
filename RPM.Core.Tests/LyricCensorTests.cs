using RPM.Core.Lyrics;

namespace RPM.Core.Tests;

/// <summary>
/// A word filter is judged on the words it does NOT touch. Mangling a slur is easy; the failures that
/// matter are the innocent words damaged by mistake. This app has many such cases: "doo-wop" is a
/// genre, "raccoon" is a word, and a listener in Pakistan should not have their country starred out
/// of a lyric. These cases would embarrass the feature, so they are tested here.
/// </summary>
public class LyricCensorTests
{
    [Fact]
    public void The_middle_of_a_slur_is_starred_and_its_shape_is_kept()
    {
        // The word stays the length it was, so the line still scans and the reader knows what was sung.
        Assert.Equal("n****s", LyricCensor.Mask("niggas"));
        Assert.Equal("n****r", LyricCensor.Mask("nigger"));
        Assert.Equal("f****t", LyricCensor.Mask("faggot"));
        Assert.Equal("r****d", LyricCensor.Mask("retard"));
    }

    [Fact]
    public void The_short_form_and_the_long_ones_are_all_caught()
    {
        // One entry covers the lot, so they cannot drift apart.
        Assert.Equal("f*g", LyricCensor.Mask("fag"));
        Assert.Equal("f**s", LyricCensor.Mask("fags"));
        Assert.Equal("f****t", LyricCensor.Mask("faggot"));
        Assert.Equal("f*****s", LyricCensor.Mask("faggots"));
        Assert.Equal("f******y", LyricCensor.Mask("faggotry"));

        // A longer word that merely starts with it is not the word.
        Assert.Equal("Fagin picked a pocket", LyricCensor.Mask("Fagin picked a pocket"));
    }

    [Fact]
    public void The_italian_american_one_is_caught_however_it_is_spelled()
    {
        // Written down from the sound of it, so it arrives in a dozen shapes.
        Assert.Equal("m*******i", LyricCensor.Mask("mulignani"));
        Assert.Equal("m*******e", LyricCensor.Mask("mulignane"));
        Assert.Equal("m******n", LyricCensor.Mask("mulignan"));
        Assert.Equal("m*******n", LyricCensor.Mask("moolinyan"));
        Assert.Equal("m*******n", LyricCensor.Mask("moulinyan"));
        Assert.Equal("m******n", LyricCensor.Mask("mulinyan"));
        Assert.Equal("M*******I", LyricCensor.Mask("MULIGNANI"));

        // …and the short form.
        Assert.Equal("m****e", LyricCensor.Mask("moolie"));
        Assert.Equal("m*****s", LyricCensor.Mask("mooleys"));
    }

    [Fact]
    public void The_vegetables_it_came_from_are_still_vegetables()
    {
        // "melanzana" is Italian for aubergine and is where the slur came from. The vegetable itself is
        // not one, and neither is a mooli (a radish, one letter from the short form).
        Assert.Equal("melanzana parmigiana", LyricCensor.Mask("melanzana parmigiana"));
        Assert.Equal("grated mooli", LyricCensor.Mask("grated mooli"));
    }

    [Fact]
    public void Gypsy_is_not_on_the_list()
    {
        // Contested, used self-descriptively, and the title of many songs: deliberately not on the list.
        Assert.Equal("Gypsy by Fleetwood Mac", LyricCensor.Mask("Gypsy by Fleetwood Mac"));
        Assert.Equal("gypsies tramps and thieves", LyricCensor.Mask("gypsies tramps and thieves"));
    }

    [Fact]
    public void Case_survives_because_the_kept_letters_are_the_original_ones()
    {
        Assert.Equal("N****s", LyricCensor.Mask("Niggas"));
        Assert.Equal("N****R", LyricCensor.Mask("NIGGER"));
    }

    [Fact]
    public void The_rest_of_the_line_is_untouched()
    {
        Assert.Equal("all my n****s in the back",
                     LyricCensor.Mask("all my niggas in the back"));

        // Punctuation sits outside the word, so it is neither eaten nor starred.
        Assert.Equal("hey, n****s! (n****s)",
                     LyricCensor.Mask("hey, niggas! (niggas)"));
    }

    [Fact]
    public void Innocent_words_that_merely_contain_an_entry_are_left_alone()
    {
        // The whole reason every pattern is bounded. A raccoon is not a slur, and neither is a cocoon.
        Assert.Equal("a raccoon in a cocoon", LyricCensor.Mask("a raccoon in a cocoon"));
    }

    [Fact]
    public void But_the_word_itself_still_is_one()
    {
        // The boundary stops the entry firing INSIDE a longer word; it does not spare the word itself.
        // Both halves of that matter, and this is the half that is easy to lose by accident.
        Assert.Equal("c**n", LyricCensor.Mask("coon"));
        Assert.Equal("c***s", LyricCensor.Mask("coons"));
        Assert.Equal("that c**n", LyricCensor.Mask("that coon"));
        Assert.Equal("C**n", LyricCensor.Mask("Coon"));

        // …and both in one line, to hold the distinction down where it can be seen.
        Assert.Equal("a raccoon and a c**n", LyricCensor.Mask("a raccoon and a coon"));
    }

    [Fact]
    public void A_country_is_not_a_slur()
    {
        // "paki" is on the list and "Pakistan" begins with it. This is why that entry spells
        // out its endings instead of allowing anything to follow.
        Assert.Equal("straight outta Pakistan", LyricCensor.Mask("straight outta Pakistan"));
        Assert.Equal("Pakistani food", LyricCensor.Mask("Pakistani food"));
    }

    [Fact]
    public void A_genre_this_app_is_full_of_is_not_a_slur()
    {
        // "wop" is a slur in some dictionaries and is deliberately NOT on the list, because doo-wop is a
        // genre, a lyric, and a folder name in half of the music collections this will run against.
        Assert.Equal("doo-wop and rock and roll", LyricCensor.Mask("doo-wop and rock and roll"));
        Assert.Equal("shoo-bop, doo-wop", LyricCensor.Mask("shoo-bop, doo-wop"));
    }

    [Fact]
    public void Ordinary_swearing_is_left_alone_unless_it_is_asked_for()
    {
        // The state the app ships in, and the state a listener who has said nothing gets. Starring these
        // uninvited would leave whole verses unreadable.
        const string line = "damn this shit is fucking loud";
        Assert.Equal(line, LyricCensor.Mask(line));
        Assert.Equal(line, LyricCensor.Mask(line, maskProfanity: false));
    }

    // ── the wider list ──
    //
    // Chosen once by the listener, then in force on every track they play. It applies to the lyric
    // sheet whether or not the track is marked explicit, because a listener who does not want to
    // read swearing wants it hidden on every track.

    [Fact]
    public void Asking_for_it_masks_ordinary_swearing()
    {
        Assert.Equal("d**n this s**t is f*****g loud",
                     LyricCensor.Mask("damn this shit is fucking loud", maskProfanity: true));

        // The slurs are still masked: the wider list is the narrow one plus swearing, not a different
        // list.
        Assert.Equal("all my n****s in the back",
                     LyricCensor.Mask("all my niggas in the back", maskProfanity: true));
    }

    [Fact]
    public void What_the_record_says_about_itself_has_no_say_in_it()
    {
        // The point of the switch. An explicit version is the one that actually HAS swearing in it, so
        // it is the track that a listener who turned this on had in mind. A rule that skipped it
        // would do nothing on the very tracks it is for.
        const string line = "this shit goes hard";
        Assert.Equal(line, LyricCensor.Mask(line));
        Assert.Equal("this s**t goes hard", LyricCensor.Mask(line, maskProfanity: true));
    }

    [Fact]
    public void The_compounds_are_not_swallowed_by_the_short_entry()
    {
        // "ass" is on the list and every one of these begins or ends with it. The trailing boundary is
        // what makes the engine fall through to the entry that spells the longer word out, so the whole
        // word is starred rather than three letters of it.
        Assert.Equal("a*s", LyricCensor.Mask("ass", maskProfanity: true));
        Assert.Equal("a*****e", LyricCensor.Mask("asshole", maskProfanity: true));
        Assert.Equal("d*******s", LyricCensor.Mask("dumbasses", maskProfanity: true));
        Assert.Equal("m**********r", LyricCensor.Mask("motherfucker", maskProfanity: true));
        Assert.Equal("b******t", LyricCensor.Mask("bullshit", maskProfanity: true));

        // Both spellings of the joined one, which is written with one m or two about equally often.
        Assert.Equal("g*****n", LyricCensor.Mask("goddamn", maskProfanity: true));
        Assert.Equal("g*******t", LyricCensor.Mask("goddammit", maskProfanity: true));
    }

    [Fact]
    public void Words_that_merely_contain_a_cuss_are_left_alone()
    {
        // The same discipline the slur list is held to, and the traps are just as real: "class" and
        // "bass" contain one entry, "assassin" and "passing" another, and a bass line is not a swear.
        const string line = "the bass in the brass class was passing assassins";
        Assert.Equal(line, LyricCensor.Mask(line, maskProfanity: true));

        // …and the near misses that cost the entries above their trailing "anything goes": a crappie is
        // a fish, Arsenal is a football club, and a record has titles on it.
        const string more = "a crappie, Arsenal, and the titles on the sleeve";
        Assert.Equal(more, LyricCensor.Mask(more, maskProfanity: true));
    }

    [Fact]
    public void The_six_with_an_innocent_sense_are_masked_when_swearing_is()
    {
        // These are on the list knowingly. Each means something ordinary as well as something rude and
        // nothing here can tell the two apart, so starring the bird, the tool and the name is the
        // accepted cost rather than a bug (see the remarks on LyricCensor.Profanity). What makes it
        // payable is that somebody asked; pinned so the trade stays a decision and not an accident.
        Assert.Equal("h**l the c**k crows and D**k p****s his finger with a h*e",
                     LyricCensor.Mask("hell the cock crows and Dick pricks his finger with a hoe",
                                      maskProfanity: true));

        Assert.Equal("a b****y mess", LyricCensor.Mask("a bloody mess", maskProfanity: true));
        Assert.Equal("d*******s", LyricCensor.Mask("dickheads", maskProfanity: true));
    }

    [Fact]
    public void And_they_stay_off_a_listener_who_never_asked()
    {
        // The switch is the whole reason those six are payable. Left alone, the same entries would be
        // starring the weather report, so they must not fire by default.
        const string line = "hell the cock crows and Dick pricks his finger with a hoe";
        Assert.Equal(line, LyricCensor.Mask(line));
    }

    [Fact]
    public void The_six_are_still_bounded_and_the_near_misses_are_many()
    {
        // Every one of these is a letter or two from an entry, and the boundaries are all that stand
        // between them and a mangled lyric. This is the test that fails first if an entry is ever
        // loosened to "and anything after".
        const string line = "hello, a shell, a cocktail, a peacock, Dickens, shoes, a hoedown, blood";
        Assert.Equal(line, LyricCensor.Mask(line, maskProfanity: true));

        // "cocky" is a temperament and "pricked" is a finger, both deliberately short of the entries.
        const string more = "a cocky lad pricked his thumb";
        Assert.Equal(more, LyricCensor.Mask(more, maskProfanity: true));
    }

    // ── drugs ──
    //
    // On the same switch as the swearing. The list has names and the few slang terms that have never meant
    // anything else. The rest of the vocabulary is ordinary English words used as slang.

    [Fact]
    public void Drugs_are_masked_with_the_swearing_and_not_before()
    {
        const string line = "cocaine and heroin and a bag of marijuana";
        Assert.Equal(line, LyricCensor.Mask(line));
        Assert.Equal("c*****e and h****n and a bag of m*******a",
                     LyricCensor.Mask(line, maskProfanity: true));

        Assert.Equal("x***x", LyricCensor.Mask("xanax", maskProfanity: true));
        Assert.Equal("l*d", LyricCensor.Mask("lsd", maskProfanity: true));
        Assert.Equal("s*****s", LyricCensor.Mask("spliffs", maskProfanity: true));
    }

    [Fact]
    public void Rock_and_roll_survive_because_they_were_never_on_the_list()
    {
        // The one failure this app could not live with. Both are drug slang; both are also the name of
        // the music, and a music player that stars them fails at its main job.
        const string line = "rock and roll, a line of verse, the crack of dawn, a trip home";
        Assert.Equal(line, LyricCensor.Mask(line, maskProfanity: true));

        // The rest of the borrowed vocabulary, kept together here: every word has an
        // ordinary use, and none of them is on the list for that reason.
        const string more = "high on a hill with pot and grass and weed and a bud, riding a horse";
        Assert.Equal(more, LyricCensor.Mask(more, maskProfanity: true));
    }

    [Fact]
    public void The_drug_names_are_bounded_like_everything_else()
    {
        // The near misses that decided how each entry is spelled. A heroine is not a drug, a method is
        // not meth, ambient is not Ambien, a bongo is not a bong. In a music player, that last case is
        // real.
        const string line = "the heroine had a method, ambient bongos, hash browns and junk mail";
        Assert.Equal(line, LyricCensor.Mask(line, maskProfanity: true));
    }

    [Fact]
    public void A_bird_is_not_a_cuss()
    {
        // "tit" requires one of its endings for exactly this reason.
        Assert.Equal("a great tit on the feeder",
                     LyricCensor.Mask("a great tit on the feeder", maskProfanity: true));
    }

    [Fact]
    public void A_line_with_nothing_to_mask_comes_back_as_it_went_in()
    {
        const string line = "We're no strangers to love";
        Assert.Equal(line, LyricCensor.Mask(line));
        Assert.Equal("", LyricCensor.Mask(""));
        Assert.Equal("", LyricCensor.Mask(null));
    }
}
