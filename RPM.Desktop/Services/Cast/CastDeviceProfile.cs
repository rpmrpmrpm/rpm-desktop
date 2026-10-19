using RPM.Core.Audio;

namespace RPM.Desktop.Services.Cast;

/// <summary>
/// The capabilities of a speaker, saved between sessions.
///
/// <para>A speaker reports its capabilities over the control channel just after a cast connects. That is
/// too late for the first choice the app makes: which format to send to this speaker. Saving the answer
/// lets the choice be made before the connection. A device that cannot take Dolby is not offered surround,
/// and a device on an optical link is not offered lossless.</para>
///
/// <para>Keyed by <see cref="CastDevice.Id"/>, not by name. A household can rename a speaker, and Google
/// gives the same name to a stereo pair and to its members.</para>
/// </summary>
/// <param name="Name">The name the speaker had when we last talked to it. Used on the settings page only,
/// never for matching.</param>
/// <param name="Flac">Whether this speaker can play our lossless stream. This is the player's own answer,
/// not the platform's. The platform publishes no MIME type for a live Matroska stream, and it answers no
/// for every device, including the devices that do play it. See <see
/// cref="CastCapabilities.FlacPlayable"/>.</param>
/// <param name="Display">Whether the speaker has a screen. Null means the speaker did not say. Null is
/// treated as yes wherever it is checked, so a device that does not answer loses nothing.</param>
public sealed partial record CastDeviceProfile(
    string Id,
    string Name,
    bool Ac3,
    bool Eac3,
    bool Flac,
    bool? Display = null)
{
    public static CastDeviceProfile From(CastDevice device, CastCapabilities capabilities) =>
        new(device.Id, device.Name, capabilities.Ac3, capabilities.Eac3,
            capabilities.FlacPlayable, capabilities.Display);
}
