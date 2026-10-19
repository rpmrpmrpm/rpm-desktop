using System.Collections.ObjectModel;
using RPM.Desktop.Presentation.Pegasus;

namespace RPM.Desktop.Tests;

/// <summary>
/// "Play next" on a row of Pegasus's queue moves the song to just after the one playing
/// (PegasusChrome.PlayNextSlot), with the same ObservableCollection.Move the queue's drag commits. Checked
/// against a real Move, because the slot depends on Move taking the item out before putting it back.
/// </summary>
public class QueuePlayNextTests
{
    private static List<string> PlayNext(string[] queue, string song, string playing)
    {
        var list = new ObservableCollection<string>(queue);
        var from = list.IndexOf(song);
        list.Move(from, PegasusChrome.PlayNextSlot(from, list.IndexOf(playing)));
        return [.. list];
    }

    [Fact]
    public void A_song_from_further_down_goes_straight_after_the_one_playing()
        => Assert.Equal(["a", "b", "e", "c", "d"], PlayNext(["a", "b", "c", "d", "e"], "e", playing: "b"));

    [Fact]
    public void A_song_from_above_the_one_playing_goes_straight_after_it()
        => Assert.Equal(["b", "c", "a", "d", "e"], PlayNext(["a", "b", "c", "d", "e"], "a", playing: "c"));

    [Fact]
    public void A_song_already_next_stays_where_it_is()
        => Assert.Equal(["a", "b", "c"], PlayNext(["a", "b", "c"], "c", playing: "b"));
}
