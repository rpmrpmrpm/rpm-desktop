using RPM.Core.Models;
using RPM.Desktop.Presentation.Pegasus;

namespace RPM.Desktop.Tests;

/// <summary>
/// Pegasus's player shows the header's cover. That cover is found and decoded from scratch after a press.
/// Without a shortcut, the opaque placeholder can show for about a second, while the record page the
/// press came from already has the sleeve on screen. So the page hands its sleeve over. What is checked:
/// the handed-over sleeve stands only while the header has no cover and the deck is still on that record.
/// </summary>
public class SleeveStandInTests
{
    private static TrackInfo Track(string path) => new() { FilePath = path, Extension = ".mp3" };

    private static readonly RecordFiles MyloXyloto = RecordFiles.Of(
    [
        Track("E:/rpm music/Coldplay/Mylo Xyloto/01 - Mylo Xyloto.mp3"),
        Track("E:/rpm music/Coldplay/Mylo Xyloto/02 - Hurts Like Heaven.mp3"),
    ]);

    [Fact]
    public void It_stands_until_the_header_has_a_cover()
    {
        const string playing = "E:/rpm music/Coldplay/Mylo Xyloto/01 - Mylo Xyloto.mp3";

        Assert.True(MyloXyloto.StandsIn(headerHasCover: false, playing));
        Assert.False(MyloXyloto.StandsIn(headerHasCover: true, playing));
    }

    [Fact]
    public void It_stands_through_the_record_but_not_past_it()
    {
        Assert.True(MyloXyloto.StandsIn(false, "E:/rpm music/Coldplay/Mylo Xyloto/02 - Hurts Like Heaven.mp3"));
        Assert.False(MyloXyloto.StandsIn(false, "E:/rpm music/Dua Lipa/Future Nostalgia/01 - Future Nostalgia.flac"));
        Assert.False(MyloXyloto.StandsIn(false, playing: null));
    }

    [Fact]
    public void A_path_is_matched_exactly()
    {
        Assert.False(MyloXyloto.Holds("e:/rpm music/coldplay/mylo xyloto/01 - mylo xyloto.mp3"));
    }

    [Fact]
    public void An_empty_frame_hands_nothing_over()
    {
        var sleeve = SleeveInHand.From(art: null, [Track("E:/rpm music/Coldplay/Mylo Xyloto/01 - Mylo Xyloto.mp3")]);

        Assert.IsType<SleeveInHand.None>(sleeve);
    }
}
