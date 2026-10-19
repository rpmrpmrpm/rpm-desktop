namespace RPM.Core.Playback;

/// <summary>
/// Where the bytes of the record playing are actually coming from.
///
/// <para>The app shows this on screen, because the same track is a different thing from each source and the
/// listener has no other way to tell which one is playing. A computer on the same network serves the file
/// as it was uploaded. The store serves a version that suits the connection. A file already on the phone is
/// the only source that still plays with no signal.</para>
/// </summary>
public enum PlayingFrom
{
    /// <summary>The usual source.</summary>
    Cloud,

    /// <summary>One of the user's own computers, on this wifi.</summary>
    Network,

    /// <summary>A file already on the phone. It is fetched ahead of playback, or downloaded for a
    /// set.</summary>
    Phone,
}
