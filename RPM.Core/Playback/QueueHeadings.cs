namespace RPM.Core.Playback;

/// <summary>Which heading stands over a song of the queue, as the queue drawer lists it.</summary>
public enum QueueHeading
{
    /// <summary>No heading: the song follows the one above it in the same run.</summary>
    None,

    /// <summary>"Up next", over the first of a run of songs the listener added with Play next or Add to now
    /// playing.</summary>
    UpNext,

    /// <summary>"After that", over the first song of the list that was played, where it goes on after a run of
    /// added songs.</summary>
    AfterThat,
}

/// <summary>
/// The headings of the queue drawer: the songs the listener added themselves (with Play next or Add to now
/// playing) stand under an "Up next" heading, apart from the list that was played, and a smaller heading
/// marks where that list goes on after them. The songs stay in their true order. The headings only say where
/// each song came from, not what is still to come, so a run of added songs can stand anywhere, even above the
/// song playing.
/// </summary>
public static class QueueHeadings
{
    /// <summary>
    /// The heading over each song of a queue whose songs are <paramref name="added"/> or not, in play order.
    /// Every run of added songs gets <see cref="QueueHeading.UpNext"/> over its first song. The song of the
    /// list after such a run gets <see cref="QueueHeading.AfterThat"/>. A run at the end has none, as nothing
    /// of the list follows it.
    /// </summary>
    public static IReadOnlyList<QueueHeading> Over(IReadOnlyList<bool> added)
    {
        ArgumentNullException.ThrowIfNull(added);

        var headings = new QueueHeading[added.Count];
        for (var at = 0; at < added.Count; at++)
        {
            var afterAdded = at > 0 && added[at - 1];
            headings[at] = added[at] switch
            {
                true when !afterAdded => QueueHeading.UpNext,
                false when afterAdded => QueueHeading.AfterThat,
                _ => QueueHeading.None,
            };
        }
        return headings;
    }
}
