namespace RPM.Core.Audio;

/// <summary>
/// Matches one speaker as named by two libraries.
///
/// <para>The app knows its endpoints by the sound library's names. The machine's own volume is reached
/// through the platform's audio API, which has its own list. The two lists are matched by name because
/// the name is the only thing they share. On Windows the names do not always agree: OpenAL Soft's
/// WASAPI backend adds an <c>"OpenAL Soft on "</c> prefix to device names in some versions and not in
/// others, while the endpoint itself always has Windows' plain friendly name. On macOS there is no
/// prefix: both sides read the CoreAudio device's own name, and the two are identical byte for byte,
/// including accents.</para>
///
/// <para>An unmatched name does not crash. The volume control just never works. So the prefix is
/// removed before comparing.</para>
/// </summary>
public static class EndpointNames
{
    /// <summary>The prefix that OpenAL Soft's Windows backend may add to an endpoint's name.</summary>
    private const string OpenAlHead = "OpenAL Soft on ";

    /// <summary>The endpoint's plain name, as the platform's own audio API lists it.
    ///
    /// <para>Only the library's prefix is removed. A trailing <c>" #2"</c> stays: the sound library adds
    /// it to tell two speakers with the same name apart, and removing it could match the wrong
    /// one.</para></summary>
    public static string Bare(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "";

        return name.StartsWith(OpenAlHead, StringComparison.Ordinal)
            ? name[OpenAlHead.Length..]
            : name;
    }
}
