using System.Text.Json;
using RPM.Core.Audio;
using RPM.Desktop.Presentation.RPaMp.Models;
using RPM.Desktop.Services.Cast;

namespace RPM.Desktop.Tests;

/// <summary>
/// The cast format is the listener's choice, and it is saved across restarts. Each cast is sent in that
/// choice, unless the speaker cannot play it. A speaker that cannot play the choice gets MP3 for that
/// cast only. The saved choice is not changed.
/// </summary>
public class CastFormatChoiceTests
{
    private static CastDeviceProfile Speaker(bool ac3, bool flac) =>
        new("id", "Living room", Ac3: ac3, Eac3: false, Flac: flac);

    [Theory]
    [InlineData(CastAudioFormat.Mp3, CastAudioFormat.Mp3)]
    [InlineData(CastAudioFormat.Flac, CastAudioFormat.Flac)]
    [InlineData(CastAudioFormat.Ac3Surround, CastAudioFormat.Ac3Surround)]
    public void A_speaker_never_asked_gets_the_choice(CastAudioFormat chosen, CastAudioFormat sent)
        => Assert.Equal(sent, RPaMpModel.CastFormatFor(chosen, speaker: null, surroundOffered: true));

    [Fact]
    public void Lossless_goes_to_a_speaker_that_plays_it()
        => Assert.Equal(CastAudioFormat.Flac,
            RPaMpModel.CastFormatFor(CastAudioFormat.Flac, Speaker(ac3: false, flac: true), surroundOffered: true));

    [Fact]
    public void Lossless_is_sent_as_mp3_to_a_speaker_that_said_it_cannot_play_it()
        => Assert.Equal(CastAudioFormat.Mp3,
            RPaMpModel.CastFormatFor(CastAudioFormat.Flac, Speaker(ac3: true, flac: false), surroundOffered: true));

    [Fact]
    public void Surround_is_sent_as_mp3_to_a_speaker_that_said_it_cannot_play_it()
        => Assert.Equal(CastAudioFormat.Mp3,
            RPaMpModel.CastFormatFor(CastAudioFormat.Ac3Surround, Speaker(ac3: false, flac: true), surroundOffered: true));

    /// <summary>A Release build hides the surround switch. A surround choice saved by a Debug build would
    /// be one the listener cannot see or turn off, so this build sends MP3 instead.</summary>
    [Fact]
    public void Surround_is_sent_as_mp3_by_a_build_that_does_not_offer_it()
        => Assert.Equal(CastAudioFormat.Mp3,
            RPaMpModel.CastFormatFor(CastAudioFormat.Ac3Surround, Speaker(ac3: true, flac: true), surroundOffered: false));

    /// <summary>The saved choice is the input for every cast. MP3 sent to one speaker does not become the
    /// choice for the next speaker.</summary>
    [Fact]
    public void A_speaker_that_cannot_play_the_choice_does_not_change_it_for_the_next_one()
    {
        var chosen = CastAudioFormat.Flac;

        var first = RPaMpModel.CastFormatFor(chosen, Speaker(ac3: true, flac: false), surroundOffered: true);
        var second = RPaMpModel.CastFormatFor(chosen, Speaker(ac3: true, flac: true), surroundOffered: true);

        Assert.Equal(CastAudioFormat.Mp3, first);
        Assert.Equal(CastAudioFormat.Flac, second);
    }

    /// <summary>A number in the settings file that names no format, for example one written by a newer
    /// build.</summary>
    [Fact]
    public void A_value_that_names_no_format_is_sent_as_mp3()
        => Assert.Equal(CastAudioFormat.Mp3,
            RPaMpModel.CastFormatFor((CastAudioFormat)7, speaker: null, surroundOffered: true));

    /// <summary>The settings file stores the format by name, like every other choice. The file stays
    /// readable, and a changed enum value cannot change what was chosen.</summary>
    [Theory]
    [InlineData(CastAudioFormat.Mp3, "\"Mp3\"")]
    [InlineData(CastAudioFormat.Flac, "\"Flac\"")]
    [InlineData(CastAudioFormat.Ac3Surround, "\"Ac3Surround\"")]
    public void The_format_is_stored_by_name(CastAudioFormat format, string json)
    {
        Assert.Equal(json, JsonSerializer.Serialize(format));
        Assert.Equal(format, JsonSerializer.Deserialize<CastAudioFormat>(json));
    }
}
