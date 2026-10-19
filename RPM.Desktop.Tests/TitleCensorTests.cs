using RPM.Desktop.Presentation.Common;

namespace RPM.Desktop.Tests;

/// <summary>
/// A record's name and a song's, as they are printed.
///
/// <para>The rule itself is <c>LyricCensor</c>'s and is tested there. What is tested here is the door a
/// title reaches a screen through. It applies the narrow rule with nobody having asked. It leaves an
/// ordinary title exactly as it was. An absent title stays absent rather than becoming an empty string.
/// The surfaces that fall back to a file name rely on this.</para>
///
/// <para>No host, so no settings to read: the wider list is off, which is the state the app ships in
/// and the state every one of these asserts against.</para>
/// </summary>
public class TitleCensorTests
{
    [Fact]
    public void A_slur_in_a_song_title_is_masked_without_anyone_asking()
        => Assert.Equal("N****s in Paris", TitleCensor.Shown("Niggas in Paris"));

    [Fact]
    public void An_ordinary_title_comes_back_the_same_string()
    {
        const string title = "No Church in the Wild (ft. Frank Ocean & The-Dream)";
        Assert.Equal(title, TitleCensor.Shown(title));
    }

    /// <summary>Swearing is the listener's own choice and nobody has made it here, so a title that has
    /// some is printed as it was tagged. The switch is what widens the list; see LyricCensor.</summary>
    [Fact]
    public void Ordinary_swearing_is_left_alone_until_the_listener_asks()
        => Assert.Equal("That's My Bitch", TitleCensor.Shown("That's My Bitch"));

    [Fact]
    public void Nothing_stays_nothing()
    {
        Assert.Null(TitleCensor.Shown(null));
        Assert.Equal("", TitleCensor.Shown(""));
    }
}
