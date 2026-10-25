using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// The line across the top of the player (TopLine): one short sentence about something the screen itself
/// does not show.
///
/// <para>For two seconds after the layout key, it names the new layout. A key that cycles cannot show on
/// itself where the next press went, and a picture that changed with no word about it reads as a fault.
/// While the music goes to a speaker, the line says where it plays, and a cast that could not start, or
/// that ended by itself, is said there for six seconds (see PegasusChrome.Cast). A listener across the room
/// has no other way to know where the sound went.</para>
///
/// <para>It is worked out on the frame tick from what is true at that moment, and written only when what it
/// says has changed. So nothing has to remember to take it down.</para>
/// </summary>
public sealed partial class PegasusChrome
{
    /// <summary>How long the new layout's name stays at the top after the layout key, in milliseconds.</summary>
    private const long LayoutNameMs = 2000;

    /// <summary>The layout's name and when it stops being shown (Environment.TickCount64).</summary>
    private string? _layoutName;
    private long _layoutNameUntil;

    /// <summary>What the line at the top says now, and whether it is a notice. It is written only when one
    /// of them changes.</summary>
    private string? _topLineShown;
    private bool _topLineNotice;

    /// <summary>
    /// Names the new layout at the top of the screen for two seconds. The host calls this after the layout
    /// key (see PegasusPlayer.StepLayout).
    /// </summary>
    public void ShowLayoutName(PegasusView layout)
    {
        _layoutName = Strings.Loc.Get(layout switch
        {
            PegasusView.Centred => "Viz_LayoutCompact",
            _ => "Viz_LayoutFull",
        });
        _layoutNameUntil = Environment.TickCount64 + LayoutNameMs;
        TickTopLine();
    }

    /// <summary>
    /// One frame of the line at the top. The layout's name comes first, for its two seconds, then a cast's
    /// failure or end, for its six, then where the music plays while it goes to a speaker. Nothing is shown
    /// over the collection, which covers the player.
    /// </summary>
    private void TickTopLine()
    {
        var now = Environment.TickCount64;
        string? line = null;
        var notice = false;

        if (!LibraryUp)
        {
            if (_layoutName is not null && now < _layoutNameUntil)
            {
                line = _layoutName;
            }
            else if (_castNotice is not null && now < _castNoticeUntil)
            {
                line = _castNotice;
                notice = true;
            }
            else if (_castingTo is { } speaker)
            {
                line = Strings.Loc.Format("Rpm_Cast_Playing", speaker);
            }
        }

        if (line == _topLineShown && notice == _topLineNotice)
            return;
        _topLineShown = line;
        _topLineNotice = notice;

        if (line is null)
        {
            TopLine.Visibility = Visibility.Collapsed;
            return;
        }

        // At the view's text scale, as the message along the foot is, and as far down from the top edge as
        // the TV puts it on a 1080 line screen.
        var scale = QueueScale;
        TopLine.Text = line;
        TopLine.FontSize = 15 * scale;
        TopLine.Margin = new Thickness(24, Math.Max(24, Root.ActualHeight * 0.041), 24, 0);
        TopLine.MaxWidth = Math.Max(200, Root.ActualWidth * 0.8);

        // A notice is a little brighter than the standing line, the brightness of an artist's name.
        TopLine.Foreground = new SolidColorBrush(notice
            ? Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
        TopLine.Visibility = Visibility.Visible;
    }
}
