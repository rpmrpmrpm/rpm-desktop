using RPM.Core.Playback;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// The headings in the queue drawer, as the Android TV app's drawer has them: the songs the listener added with
/// Play next or Add to now playing (PlaylistTrack.IsUpNext) stand under "Up next", in their true place in the
/// queue, and "After that" marks where the list that was played goes on after them (RPM.Core's QueueHeadings).
/// The headings take no focus and no pointer.
///
/// <para><b>Slots, not children.</b> A heading is a child of QueueRows like the rows, so the index of a row among
/// the children is no longer its place in the queue. Everything that works with places (the drag, the move by
/// the keys, the removal, the scroll to the playing row) reads <see cref="_queueSlots"/>, the rows alone in queue
/// order, and <see cref="QueueSlotTops"/>, where each row's line starts. With no heading in the drawer, which is
/// the usual case, a slot is a child and the lines are one pitch apart, exactly as before.</para>
///
/// <para>A heading stays where it is while a row is carried past it: the rows step from line to line around it.
/// Once the row is put down, the drawer is built again, because the row's mark moves with it and the headings
/// may now stand elsewhere.</para>
/// </summary>
public sealed partial class PegasusChrome
{
    /// <summary>The queue's rows in queue order, without the headings between them. Built with the rows (see
    /// BuildQueue).</summary>
    private readonly List<Border> _queueSlots = [];

    /// <summary>Whether the drawer shows any heading. Set as the rows are built.</summary>
    private bool _queueHeadings;

    /// <summary>
    /// Where each row's line starts in the list, by slot, for a row <paramref name="pitch"/> apart from the next.
    /// With no heading, slot k starts k pitches down. With headings, each heading's own height and the list's
    /// gap after it are added below it, as the StackPanel lays them out.
    /// </summary>
    private double[] QueueSlotTops(double pitch)
    {
        var tops = new double[_queueSlots.Count];
        if (!_queueHeadings)
        {
            for (var i = 0; i < tops.Length; i++)
                tops[i] = i * pitch;
            return tops;
        }

        double y = 0;
        var slot = 0;
        foreach (var child in QueueRows.Children)
        {
            if (slot < tops.Length && ReferenceEquals(child, _queueSlots[slot]))
            {
                tops[slot++] = y;
                y += pitch;
            }
            else
            {
                y += child.DesiredSize.Height + QueueRows.Spacing;
            }
        }
        return tops;
    }

    /// <summary>The pitch of the rows: one row's height and the gap after it. Zero before the rows are laid out.
    /// Every row is the same height; a heading is not a row.</summary>
    private double QueueRowPitch() =>
        _queueSlots.Count > 0 ? _queueSlots[0].ActualHeight + QueueRows.Spacing : 0;

    /// <summary>The headings over the queue's songs, one for each song, from the marks on its rows.</summary>
    private static IReadOnlyList<QueueHeading> QueueHeadingsOf(IReadOnlyList<RPaMp.Models.PlaylistTrack> queue) =>
        QueueHeadings.Over([.. queue.Select(track => track.IsUpNext)]);

    /// <summary>
    /// One heading line: "Up next" in white, or "After that" at the artist lines' 70 %, both 26 points SemiBold
    /// at the collection's scale, as the TV's title size; in from the left as the rows' titles are, with room
    /// above to set it off from the row before.
    /// </summary>
    private TextBlock QueueHeadingLine(QueueHeading heading, double scale)
    {
        var upNext = heading == QueueHeading.UpNext;
        return new TextBlock
        {
            Text = Strings.Loc.Get(upNext ? "Viz_Queue" : "Tv_QueueAfterUpNext"),
            FontSize = 26 * QueueScale,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(upNext ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(10 * scale, (upNext ? 16 : 14) * scale, 10 * scale, (upNext ? 4 : 2) * scale),
            IsHitTestVisible = false,
        };
    }
}
