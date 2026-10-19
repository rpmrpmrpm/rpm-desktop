namespace RPM.Desktop.Services.Audio;

/// <summary>
/// The volume last used for one set of speakers, identified by the sound driver's name for them.
///
/// <para>Volume is stored per device, for the same reason as the equaliser curve (see
/// <see cref="EqProfile"/>). Headphones and desk speakers have different loudness. One remembered value
/// would need to be set again every time the music moves between them.</para>
///
/// <para>Cast speakers are not stored here. A speaker in a room keeps its own volume. Its buttons and
/// the Google Home app change the same setting that RPM uses. So when a cast starts, the app uses the
/// speaker's current volume, and does not keep a second value that could be out of date.</para>
/// </summary>
/// <param name="Device">The driver's name for the device, as
/// <c>PlaybackService.OutputDeviceName</c> reports it. An empty name means the audio is not on this
/// computer. For that reason, nothing is stored under an empty name.</param>
public sealed record OutputVolume(string Device, int Volume);
