namespace RPM.Core.Ripping;

/// <summary>
/// The ffmpeg encoder arguments and file extension for each <see cref="RipFormat"/>. Every path that makes
/// final audio files uses this: CD and DVD ripping (<see cref="CdRipper"/>) and vinyl import. One
/// definition means the three formats encode the same way, whatever the source.
///
/// FLAC and ALAC are native ffmpeg encoders. libmp3lame is LGPL-safe. None of them makes ffmpeg a GPL build.
/// </summary>
public static class RipEncoders
{
    /// <summary>ffmpeg output codec arguments for the chosen format. They go before the output path.</summary>
    public static IEnumerable<string> ArgsFor(RipFormat format) => format switch
    {
        RipFormat.Flac => ["-c:a", "flac", "-compression_level", "8"],
        RipFormat.Alac => ["-c:a", "alac"],
        RipFormat.Mp3 => ["-c:a", "libmp3lame", "-b:a", "320k"],
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    /// <summary>File extension, with a leading dot, that the format's files use.</summary>
    public static string ExtensionOf(RipFormat format) => format switch
    {
        RipFormat.Flac => ".flac",
        RipFormat.Alac => ".m4a",
        RipFormat.Mp3 => ".mp3",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };
}
