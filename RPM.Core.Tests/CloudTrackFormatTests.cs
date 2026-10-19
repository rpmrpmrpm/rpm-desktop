using RPM.Core.Cloud;

namespace RPM.Core.Tests;

/// <summary>
/// The line a cloud album prints under its name: "FLAC · 16/44.1 · 5.1".
///
/// <para>The wording itself is <see cref="RPM.Core.Metadata.AudioFormat"/>'s and is tested there.
/// What is tested here is the join: that a catalogue row knows whether it can say anything at all,
/// and that a row without format fields says nothing instead of guessing.</para>
/// </summary>
public class CloudTrackFormatTests
{
    /// <summary>"16/44.1" where a reader writes decimals with a point, "16/44,1" where they write them
    /// with a comma. The wording follows the reader's culture, so the expected value is built the same
    /// way and is not fixed to one locale's punctuation.</summary>
    private static string Resolution(int bits, double khz) => $"{bits}/{khz:0.0}";

    [Fact]
    public void A_row_that_predates_the_format_says_nothing()
    {
        var tags = new CloudTrackTags(213.5, TrackNumber: 4, Year: 1979);

        Assert.False(tags.HasFormat);
        Assert.Empty(tags.Format);
    }

    [Fact]
    public void A_lossless_row_states_its_resolution_and_its_layout()
    {
        var tags = new CloudTrackTags(
            Extension: ".flac", Channels: 6, SampleRate: 44100, BitsPerSample: 16);

        Assert.True(tags.HasFormat);
        Assert.Equal(["FLAC", Resolution(16, 44.1), "5.1"], tags.Format);
    }

    [Fact]
    public void Stereo_is_said_by_saying_nothing()
    {
        var tags = new CloudTrackTags(Extension: ".flac", Channels: 2, SampleRate: 96000, BitsPerSample: 24);

        Assert.Equal(["FLAC", "24/96"], tags.Format);
    }

    [Fact]
    public void A_lossy_row_states_its_bitrate_instead_of_a_bit_depth()
    {
        var tags = new CloudTrackTags(Extension: ".mp3", Channels: 2, SampleRate: 44100, BitrateKbps: 320);

        Assert.Equal(["MP3", "320 kbps"], tags.Format);
    }

    [Fact]
    public void Apple_lossless_is_told_from_aac_by_what_the_decoder_said()
    {
        // Both are stored in a .m4a, and a lossless rip must not be called "AAC".
        var alac = new CloudTrackTags(
            Extension: ".m4a", Channels: 2, SampleRate: 44100, BitsPerSample: 16,
            CodecDescription: "MPEG-4 Audio (alac)");
        var aac = new CloudTrackTags(
            Extension: ".m4a", Channels: 2, SampleRate: 44100, BitrateKbps: 256,
            CodecDescription: "MPEG-4 Audio (mp4a)");

        Assert.Equal(["ALAC", Resolution(16, 44.1)], alac.Format);
        Assert.Equal(["AAC", "256 kbps"], aac.Format);
    }

    [Fact]
    public void Knowing_only_the_extension_is_still_worth_a_word()
    {
        // What a row has after a re-upload, before the desktop has pushed the rest.
        var tags = new CloudTrackTags(Extension: ".flac");

        Assert.True(tags.HasFormat);
        Assert.Equal(["FLAC"], tags.Format);
    }
}
