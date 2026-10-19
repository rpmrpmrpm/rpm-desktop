namespace RPM.Core.Metadata;

/// <summary>
/// What a file is, said the way a record sleeve would say it: "FLAC", "5.1".
///
/// <para>There are two facts, and they are different kinds of fact. The codec is mostly given by the file
/// extension. A .flac file is always FLAC. There is one real exception that matters: <c>.m4a</c> can hold AAC
/// or Apple Lossless. Calling a lossless file "AAC" would be the wrong way round. So the codec is decided
/// from the extension, and also from what the decoder says is inside. Only the decoder can tell the two
/// apart.</para>
///
/// <para>The channel count is read straight from the stream. It is written in the layout people actually say,
/// such as 5.1, not "six channels". STEREO IS LEFT OUT. A line that says "stereo" on every record says nothing
/// about any of them. Only the exceptions are worth the space.</para>
///
/// <para>This lives here, not in the view, so it can be tested against real extensions without a library. It
/// also means the mirror and any future track grid use the same wording.</para>
/// </summary>
public static class AudioFormat
{
    /// <summary>
    /// The short label for a file, or null when the extension is not one we know.
    /// </summary>
    /// <param name="extension">Lower case, with a leading dot, as <c>TrackInfo.Extension</c> holds it.</param>
    /// <param name="description">What the decoder says the stream is: TagLib's <c>Properties.Description</c>. It
    /// is used only where the extension is truly ambiguous.</param>
    public static string? Codec(string? extension, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(extension)) return null;

        var ext = extension.StartsWith('.') ? extension : "." + extension;

        // The MPEG-4 family is the ambiguous one, and it is ambiguous both ways. .m4a is AAC unless it is Apple
        // Lossless. .mp4 is usually AAC. TagLib names the codec in its description, such as "ALAC" or
        // "MPEG-4 Audio (alac)". So the description decides.
        if (ext is ".m4a" or ".m4b" or ".mp4" or ".aac")
        {
            if (description is { Length: > 0 } said)
            {
                if (Says(said, "alac", "apple lossless")) return "ALAC";
                // A surround or Atmos download is Dolby inside the same .m4a. It is checked before plain AC-3,
                // because "eac3" contains "ac3". It is checked at all because calling it "AAC" is the one thing
                // it certainly is not.
                if (Says(said, "eac3", "e-ac-3", "ec-3")) return "Dolby Digital Plus";
                if (Says(said, "ac3", "ac-3")) return "Dolby Digital";
            }
            return "AAC";
        }

        return ext.ToLowerInvariant() switch
        {
            ".flac" => "FLAC",
            ".mp3" => "MP3",
            ".wav" => "WAV",
            ".aif" or ".aiff" or ".aifc" => "AIFF",
            ".ogg" => "OGG",
            ".oga" => "OGG",
            ".opus" => "Opus",
            ".wma" => "WMA",
            ".ape" => "APE",
            ".wv" => "WavPack",
            ".mpc" => "Musepack",
            ".dsf" or ".dff" => "DSD",
            ".alac" => "ALAC",
            _ => null,
        };
    }

    /// <summary>
    /// The channel layout, as a sleeve would print it. Null when there is nothing worth saying.
    ///
    /// <para>Null for stereo, which almost every record is. A badge that every record wears is not useful. Null
    /// is also returned for a count that has no standard name, rather than inventing one.</para>
    /// </summary>
    public static string? Channels(int? channels) => channels switch
    {
        1 => "Mono",
        3 => "2.1",
        4 => "4.0",
        5 => "5.0",
        6 => "5.1",
        7 => "6.1",
        8 => "7.1",
        10 => "9.1",
        12 => "7.1.4",          // Atmos beds arrive as twelve discrete channels
        _ => null,              // 2 (stereo) is said by saying nothing. 0 or unknown has nothing to say.
    };

    private static bool Says(string description, params string[] words) =>
        words.Any(w => description.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>The codecs that keep every sample. They decide how a file's quality is worth stating.</summary>
    private static bool IsLossless(string? codec) =>
        codec is "FLAC" or "ALAC" or "WAV" or "AIFF" or "APE" or "WavPack" or "DSD";

    /// <summary>
    /// How good the encode is, said the way each format is judged. The two kinds of format are judged
    /// differently.
    ///
    /// <para>A lossless file is judged by its resolution. "24/96" means 24 bits at 96 kilohertz. Its bitrate
    /// follows from the music, and nobody chose it. A lossy file is judged the other way round. Its bit depth
    /// means nothing, because it decodes to any depth. Its bitrate is the whole decision. So each is asked the
    /// question it can answer.</para>
    /// </summary>
    public static string? Quality(string? codec, int? bitsPerSample, int? sampleRate, int? bitrateKbps)
    {
        if (IsLossless(codec))
        {
            if (bitsPerSample is > 0 and var bits && sampleRate is > 0 and var rate)
            {
                var khz = rate / 1000.0;
                // 44.1 keeps its decimal point. 48 and 96 do not get one.
                var said = Math.Abs(khz - Math.Round(khz)) < 0.05
                    ? Math.Round(khz).ToString("0")
                    : khz.ToString("0.0");
                return $"{bits}/{said}";
            }

            // No bit depth is known, for example because the decoder could not measure the stream. The codec name
            // already says the useful part, so nothing is returned. A bitrate that nobody chose would not help.
            return null;
        }

        return bitrateKbps is > 0 and var kbps ? $"{kbps} kbps" : null;
    }

    /// <summary>Everything worth saying, in sleeve order. Unknown parts are dropped.</summary>
    public static IReadOnlyList<string> Describe(
        string? extension, int? channels, string? description = null,
        int? bitsPerSample = null, int? sampleRate = null, int? bitrateKbps = null)
    {
        var parts = new List<string>(3);

        var codec = Codec(extension, description);
        if (codec is not null) parts.Add(codec);
        if (Quality(codec, bitsPerSample, sampleRate, bitrateKbps) is { } quality) parts.Add(quality);
        if (Channels(channels) is { } layout) parts.Add(layout);

        return parts;
    }
}
