using RPM.Core.Library;

namespace RPM.Core.Playback;

/// <summary>
/// Where the songs in the queue came from.
///
/// <para><b>The queue records whether a generated list is playing.</b> The like and dislike keys at the
/// transport do nothing outside a generated list. A refusal takes a song out of the lists the library
/// writes itself, and a like keeps it in them. Neither touches an album the listener chose. A key that is
/// offered where it changes nothing is worse than no key: it invites an opinion and then drops it. So the
/// keys are shown only while a generated list is playing, and this value tells the deck which case it is
/// in.</para>
///
/// <para><b>The source is stated at every call and never defaulted.</b> The queue is filled from many call
/// sites. A default would mean the call that forgets is the one that decides, and the decision would be
/// hidden at the call. This is the same reason <see cref="SmartPlaylistId.WholeShelf"/> is used for
/// narrowing.</para>
///
/// <para><b>It carries WHICH list, not only that there is one.</b> Nothing reads the list yet. The keys ask
/// only whether there is a list at all. A verdict about a pick is a verdict about a pick made for one
/// particular list, so the list is kept here for when that matters.</para>
/// </summary>
/// <param name="Generated">The list the library wrote, or null where the queue came from anywhere
/// else.</param>
public readonly record struct QueueSource(SmartPlaylistId? Generated)
{
    /// <summary>An album, a stored playlist, a folder of files, a search, the import bench, or anywhere
    /// else that is not a list the library writes for itself. This is the stated answer for "not
    /// generated", so no call site can mean it by leaving something out.</summary>
    public static QueueSource Elsewhere { get; }

    /// <summary>One of the lists the library writes itself.</summary>
    public static QueueSource Generation(SmartPlaylistId id) => new(id);

    /// <summary>Whether the library chose what is playing, not the listener. This is the only question that
    /// code asks of this type today.</summary>
    public bool IsGenerated => Generated is not null;
}
