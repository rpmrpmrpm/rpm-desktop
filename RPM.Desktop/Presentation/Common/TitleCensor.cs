using System.Diagnostics.CodeAnalysis;
using RPM.Core.Lyrics;

namespace RPM.Desktop.Presentation.Common;

/// <summary>Returns a record's name or a song's name as it is shown on screen. The name is masked by
/// the same rule that the lyric sheet uses. For example, "Niggas in Paris" is drawn as "N****s in
/// Paris".
///
/// <para>The rule is defined in <see cref="LyricCensor"/>, and it is not repeated here. Slurs are
/// always masked, on every screen. Ordinary swearing and named drugs are masked only when the listener
/// has turned on that option (<see cref="SettingsService.MaskProfanity"/>, which is off by default). A
/// title and a lyric use the same words, so a lyric sheet that masks a word while the tracklist above
/// it shows the word in full would contradict itself.</para>
///
/// <para><b>Masking happens only at the moment of drawing.</b> This runs on the path from a model to a
/// TextBlock. That path is the converter in the markup, or a call in the code that sets the text. It
/// never runs on the path into a model. A masked name is never compared, searched, looked up, written
/// to a tag, saved to a playlist, or sent to the cloud. The tags on disk keep the names their author
/// wrote. A lookup asks the catalogues for the record's real name. The search field finds a song by the
/// word in its real title. A masked string that reached any of these would cause silent corruption,
/// with nothing on screen to show for it. That is why the models keep the true name, and only the last
/// step is masked.</para>
///
/// <para>The setting is read on every call, not cached. So the first tracklist drawn after the setting
/// is changed uses the new rule. The cost is one field read, because the service itself is looked up
/// only once.</para>
/// </summary>
public static class TitleCensor
{
    /// <summary>Returns the name to show. A null or empty name is returned unchanged. An absent title
    /// is not the same as a clean one, and the views that fall back to a file name check for
    /// it.</summary>
    [return: NotNullIfNotNull(nameof(name))]
    public static string? Shown(string? name)
        => string.IsNullOrEmpty(name) ? name : LyricCensor.Mask(name, Wide);

    /// <summary>Whether the listener has asked for ordinary swearing and named drugs to be masked, as
    /// well as slurs. This is false when the app has no services to ask, such as a design-time view or
    /// a screen drawn before the host starts. The narrow rule, slurs only, applies to everyone. So a
    /// title that cannot read the setting is drawn with that rule, rather than not drawn at
    /// all.</summary>
    private static bool Wide
    {
        get
        {
            try
            {
                _settings ??= App.Services.GetService<SettingsService>();
            }
            catch
            {
                return false;   // no host yet, so only the slurs are masked
            }
            return _settings?.MaskProfanity ?? false;
        }
    }

    private static SettingsService? _settings;
}
