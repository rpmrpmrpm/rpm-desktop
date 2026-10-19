using RPM.Core.Cloud;

namespace RPM.Core.Tests;

/// <summary>
/// The listener's marks, read when needed and not stored on each item.
///
/// <para>The important questions here are about order. A mark is a push and then, later, a catalogue
/// that contains it. A read of the whole catalogue is often already running when the tap happens,
/// because the phone refreshes its listing after it draws the wall. So every test below asks the same
/// question from a different side: does the answer depend on which of the two arrives first?</para>
/// </summary>
public class FavoriteMarksTests
{
    private static CloudTrack Track(string id, bool favorite = false, long at = 0) =>
        new(RpmId: id, Sha256: id, RelativePath: null, OriginalSize: 0,
            Title: id, Artist: null, Album: null, AddedAt: 0,
            Tags: new CloudTrackTags(Favorite: favorite, FavoritedAt: at));

    // ── what the catalogue says ──

    [Fact]
    public void Nothing_is_loved_before_a_catalog_has_been_read()
        => Assert.False(new FavoriteMarks().IsMarked("a"));

    [Fact]
    public void A_catalog_row_marked_a_favourite_is_loved()
    {
        var marks = new FavoriteMarks();
        marks.Read([Track("a", favorite: true), Track("b")]);

        Assert.True(marks.IsMarked("a"));
        Assert.False(marks.IsMarked("b"));
    }

    /// <summary>The desktop cleared it. A catalogue is read whole, so a mark it does not contain is not
    /// set.</summary>
    [Fact]
    public void A_catalog_that_no_longer_marks_a_song_unloves_it()
    {
        var marks = new FavoriteMarks();
        marks.Read([Track("a", favorite: true)]);
        marks.Read([Track("a")]);

        Assert.False(marks.IsMarked("a"));
    }

    // ── what this phone says ──

    [Fact]
    public void A_mark_made_here_is_seen_at_once()
    {
        var marks = new FavoriteMarks();
        marks.Note("a", favorite: true, at: 7);

        Assert.True(marks.IsMarked("a"));
    }

    [Fact]
    public void Clearing_a_mark_the_catalog_made_is_seen_at_once()
    {
        var marks = new FavoriteMarks();
        marks.Read([Track("a", favorite: true, at: 3)]);
        marks.Note("a", favorite: false, at: 0);

        Assert.False(marks.IsMarked("a"));
    }

    // ── and which of the two wins ──

    /// <summary>The main case. The refresh started before the tap, so its listing cannot know about the
    /// tap, and it must not undo it.</summary>
    [Fact]
    public void A_catalog_read_before_the_tap_does_not_undo_the_tap()
    {
        var marks = new FavoriteMarks();
        marks.Note("a", favorite: true, at: 10);

        // The listing that was already being read. It has "a" unmarked, because it was read first.
        marks.Read([Track("a")]);

        Assert.True(marks.IsMarked("a"));
    }

    /// <inheritdoc cref="A_catalog_read_before_the_tap_does_not_undo_the_tap"/>
    [Fact]
    public void A_catalog_read_before_an_unmark_does_not_bring_the_mark_back()
    {
        var marks = new FavoriteMarks();
        marks.Read([Track("a", favorite: true, at: 3)]);
        marks.Note("a", favorite: false, at: 0);
        marks.Read([Track("a", favorite: true, at: 3)]);

        Assert.False(marks.IsMarked("a"));
    }

    /// <summary>Once a listing agrees, this phone has nothing more to add, and the NEXT listing, which
    /// may contain the desktop clearing the same mark, is trusted.</summary>
    [Fact]
    public void A_catalog_that_has_caught_up_takes_the_mark_back_over()
    {
        var marks = new FavoriteMarks();
        marks.Note("a", favorite: true, at: 10);

        // The push has arrived: this listing has it.
        marks.Read([Track("a", favorite: true, at: 10)]);

        // And now the mark is cleared from a computer.
        marks.Read([Track("a")]);

        Assert.False(marks.IsMarked("a"));
    }

    // ── the stamp, which is what orders the favourites list ──

    [Fact]
    public void A_songs_stamp_is_the_one_the_catalog_carried()
    {
        var marks = new FavoriteMarks();
        marks.Read([Track("a", favorite: true, at: 42)]);

        Assert.Equal(42, marks.MarkedAt("a"));
    }

    /// <summary>A song marked a moment ago belongs at the TOP of a list ordered newest first, not at the
    /// bottom under a catalogue that has not been read since.</summary>
    [Fact]
    public void A_mark_made_here_carries_its_own_stamp()
    {
        var marks = new FavoriteMarks();
        marks.Read([Track("a", favorite: true, at: 42)]);
        marks.Note("b", favorite: true, at: 99);

        Assert.Equal(99, marks.MarkedAt("b"));
        Assert.Equal(42, marks.MarkedAt("a"));
    }

    [Fact]
    public void An_unloved_song_has_no_stamp()
    {
        var marks = new FavoriteMarks();
        marks.Read([Track("a", favorite: true, at: 42)]);
        marks.Note("a", favorite: false, at: 77);

        Assert.Equal(0, marks.MarkedAt("a"));
    }

    // ── putting one back ──

    /// <summary>What a refused push restores. Not "the opposite value": a song marked from two screens
    /// at once would be restored to a state it was never in.</summary>
    [Fact]
    public void What_a_song_was_before_a_mark_is_the_mark_and_its_stamp()
    {
        var marks = new FavoriteMarks();
        marks.Read([Track("a", favorite: true, at: 42)]);

        var before = marks.MarkOf("a");
        marks.Note("a", favorite: false, at: 0);
        marks.Note("a", before.On, before.At);

        Assert.True(marks.IsMarked("a"));
        Assert.Equal(42, marks.MarkedAt("a"));
    }

    [Fact]
    public void A_song_nobody_has_marked_was_nothing_at_all()
        => Assert.Equal((false, 0), new FavoriteMarks().MarkOf("a"));

    // ── being told ──

    [Fact]
    public void A_mark_says_so()
    {
        var marks = new FavoriteMarks();
        var told = 0;
        marks.Changed += () => told++;

        marks.Note("a", favorite: true, at: 1);

        Assert.Equal(1, told);
    }

    /// <summary>A catalogue can contain a mark made on a computer, so every heart is drawn again when
    /// one arrives, the same as after a tap.</summary>
    [Fact]
    public void A_catalog_arriving_says_so()
    {
        var marks = new FavoriteMarks();
        var told = 0;
        marks.Changed += () => told++;

        marks.Read([Track("a", favorite: true)]);

        Assert.Equal(1, told);
    }

    // ── signing out ──

    [Fact]
    public void Signing_out_leaves_nothing_of_either_layer()
    {
        var marks = new FavoriteMarks();
        marks.Read([Track("a", favorite: true, at: 42)]);
        marks.Note("b", favorite: true, at: 99);

        marks.Forget();

        Assert.False(marks.IsMarked("a"));
        Assert.False(marks.IsMarked("b"));
    }
}
