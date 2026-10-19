using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Main.Models;

/// <summary>How a track's facts are written in a grid cell. There is one place for this, because there are
/// two grids: the library's table and the import queue. They show the same columns of the same kind of
/// data. A file that reads "24-bit / 96 kHz / 41.2 MB" as it comes in must read the same once it is filed.
/// Otherwise the two views of one collection would disagree, and users would notice.
/// </summary>
public static class TrackFieldText
{
    /// <summary>A tag number, or nothing at all. Zero means "not set", and a column of zeros would say the
    /// opposite.</summary>
    public static string Num(uint value) => value > 0 ? value.ToString() : "";

    /// <summary>The container, without its dot. This is how users name a format ("FLAC", "MP3").</summary>
    public static string Kind(string? extension) => (extension ?? "").TrimStart('.').ToUpperInvariant();

    public static string Bitrate(int? kbps) => kbps is > 0 and { } k ? $"{k} kbps" : "";

    public static string SampleRate(int? hz) => hz is > 0 and { } rate
        ? (rate % 1000 == 0 ? $"{rate / 1000} kHz" : $"{rate / 1000d:0.#} kHz")
        : "";

    public static string BitDepth(int? bits) => BitDepthPhrase(bits)?.Text ?? "";

    /// <summary>The bit depth, before the words. French writes "24 bits" and German writes "24
    /// Bit".</summary>
    internal static Phrase? BitDepthPhrase(int? bits) =>
        bits is > 0 and { } b ? new Phrase("Track_BitDepth", b.ToString()) : null;

    public static string Channels(int? channels) => ChannelsPhrase(channels)?.Text ?? "";

    /// <summary>The channel layout, before the words. The counts that have a word get the word. A surround
    /// layout reads the same in every language. The rest get the count.</summary>
    internal static Phrase? ChannelsPhrase(int? channels) => channels switch
    {
        null or 0 => null,
        1 => new Phrase("Track_Mono", ""),
        2 => new Phrase("Track_Stereo", ""),
        6 => Phrase.AsIs("5.1"),
        8 => Phrase.AsIs("7.1"),
        var n => new Phrase("Track_Channels", n.Value.ToString()),
    };

    public static string Duration(double seconds) => seconds > 0 ? Clock(seconds) : "";

    public static string Size(long bytes) => SizePhrase(bytes)?.Text ?? "";

    /// <summary>The size, before the words. The unit is a string key because French uses octets (Ko, Mo,
    /// Go).</summary>
    internal static Phrase? SizePhrase(long bytes) => bytes > 0 ? Bytes(bytes) : null;

    private static string Clock(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Round(seconds));
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes}:{span.Seconds:00}";
    }

    private static Phrase Bytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => new("Unit_GB", $"{bytes / (1024d * 1024 * 1024):0.##}"),
        >= 1024 * 1024 => new("Unit_MB", $"{bytes / (1024d * 1024):0.#}"),
        >= 1024 => new("Unit_KB", $"{bytes / 1024d:0}"),
        _ => new("Unit_Bytes", $"{bytes}"),
    };
}
