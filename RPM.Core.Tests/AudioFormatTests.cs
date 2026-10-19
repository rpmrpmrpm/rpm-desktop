using RPM.Core.Metadata;

namespace RPM.Core.Tests;

/// <summary>What a file is, said the way a sleeve would say it.</summary>
public class AudioFormatTests
{
    [Theory]
    [InlineData(".flac", "FLAC")]
    [InlineData(".mp3", "MP3")]
    [InlineData(".wav", "WAV")]
    [InlineData(".aiff", "AIFF")]
    [InlineData(".opus", "Opus")]
    [InlineData(".wv", "WavPack")]
    [InlineData(".dsf", "DSD")]
    public void The_extension_names_the_codec(string extension, string expected)
        => Assert.Equal(expected, AudioFormat.Codec(extension));

    [Fact]
    public void An_unknown_extension_is_not_guessed_at()
        => Assert.Null(AudioFormat.Codec(".txt"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void And_nor_is_a_missing_one(string? extension)
        => Assert.Null(AudioFormat.Codec(extension));

    /// <summary>The one case the extension genuinely cannot decide, and the one that matters: calling
    /// somebody's lossless rip "AAC" is exactly the wrong way round.</summary>
    [Theory]
    [InlineData("ALAC", "ALAC")]
    [InlineData("MPEG-4 Audio (alac)", "ALAC")]
    [InlineData("MPEG-4 Audio (mp4a)", "AAC")]
    [InlineData(null, "AAC")]
    [InlineData("", "AAC")]
    public void An_m4a_is_read_from_the_stream_not_the_name(string? description, string expected)
        => Assert.Equal(expected, AudioFormat.Codec(".m4a", description));

    /// <summary>A surround or Atmos download is Dolby inside the same .m4a. "AAC" is the one thing
    /// it certainly is not, and the word for it belongs on the sleeve line.</summary>
    [Theory]
    [InlineData("eac3", "Dolby Digital Plus")]
    [InlineData("E-AC-3", "Dolby Digital Plus")]
    [InlineData("eac3 (ec-3 / 0x332D6365)", "Dolby Digital Plus")]
    [InlineData("ac3", "Dolby Digital")]
    public void Dolby_in_an_m4a_is_not_called_aac(string description, string expected)
        => Assert.Equal(expected, AudioFormat.Codec(".m4a", description));

    /// <summary>Lossy, whichever Dolby it is: the bitrate is the decision, not a bit depth.</summary>
    [Fact]
    public void Dolby_is_judged_by_its_bitrate()
        => Assert.Equal(["Dolby Digital Plus", "768 kbps", "5.1"],
            AudioFormat.Describe(".m4a", 6, "eac3", bitsPerSample: 0, sampleRate: 48000, bitrateKbps: 768));

    [Fact]
    public void The_extension_may_arrive_without_its_dot()
        => Assert.Equal("FLAC", AudioFormat.Codec("flac"));

    [Theory]
    [InlineData(1, "Mono")]
    [InlineData(6, "5.1")]
    [InlineData(8, "7.1")]
    [InlineData(12, "7.1.4")]
    public void A_layout_worth_saying_is_said(int channels, string expected)
        => Assert.Equal(expected, AudioFormat.Channels(channels));

    /// <summary>Stereo is what almost everything is, so saying it on every record says it on none.</summary>
    [Fact]
    public void Stereo_says_nothing()
        => Assert.Null(AudioFormat.Channels(2));

    [Theory]
    [InlineData(null)]
    [InlineData(0)]         // the decoder would not say
    [InlineData(11)]        // a count with no name: better to show nothing than to invent one
    public void And_neither_does_a_count_there_is_no_word_for(int? channels)
        => Assert.Null(AudioFormat.Channels(channels));

    [Fact]
    public void Describe_puts_them_in_sleeve_order()
        => Assert.Equal(["FLAC", "5.1"], AudioFormat.Describe(".flac", 6));

    [Fact]
    public void Describe_drops_whatever_is_not_known()
    {
        Assert.Equal(["FLAC"], AudioFormat.Describe(".flac", 2));           // stereo: codec only
        Assert.Equal(["5.1"], AudioFormat.Describe(".xyz", 6));             // codec unknown
        Assert.Empty(AudioFormat.Describe(null, null));
    }
}
