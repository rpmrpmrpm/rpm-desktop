namespace RPM.Core.Art;

/// <summary>
/// An artist's backdrops as a deck the user can page through, and which one is up.
///
/// <para>
/// The chooser decides which photo an artist gets (<see cref="BackdropRanker"/> and the crop that
/// follows it), and it is right most of the time. When it is not, the person looking at it can say so
/// with a swipe across the picture on the phone, and this type is what that gesture moves. Nothing
/// here fetches, decodes or crops. It holds the list, the position in it, and the two rules that keep
/// the position correct.
/// </para>
///
/// <para>
/// <b>Peek, then commit.</b> Paging is asked for with <see cref="Peek"/>, which does not move: the
/// index only advances once the caller has the picture in hand and calls <see cref="Commit"/>. A photo
/// that fails to download therefore leaves the deck exactly where it was, so what is on screen and
/// what the deck says is on screen never disagree.
/// </para>
///
/// <para>
/// It stops at both ends rather than wrapping. With a ring of photographs there is no way to tell
/// that you have seen them all, and there are usually only a few.
/// </para>
/// </summary>
public sealed class BackdropDeck
{
    private IReadOnlyList<string> _urls = [];
    private int _index = -1;

    /// <summary>Whose deck this is. Comparison is case-insensitive, so the same artist spelled two ways
    /// across two tracks is not a different artist.</summary>
    public string? ArtistKey { get; private set; }

    public int Count => _urls.Count;

    /// <summary>Where in the deck the picture on screen is, or -1 when it is not in the deck at all.
    /// That is the state before the first choice has landed.</summary>
    public int Index => _index;

    public string? Current => _index >= 0 && _index < _urls.Count ? _urls[_index] : null;

    /// <summary>Whether there is anywhere to page to. One photograph is not a deck.</summary>
    public bool CanPage => _urls.Count > 1;

    /// <summary>
    /// Puts an artist's backdrops in, with <paramref name="current"/> as the one on screen.
    ///
    /// <para>Re-loading the same artist keeps the position: playing through a record re-runs the art
    /// pipeline on every album change, and a hand-picked photo must survive that. A different artist
    /// starts again.</para>
    /// </summary>
    public void Load(string? artistKey, IReadOnlyList<string>? urls, string? current)
    {
        var sameArtist = ArtistKey is not null
            && string.Equals(ArtistKey, artistKey, StringComparison.OrdinalIgnoreCase);

        ArtistKey = artistKey;
        _urls = urls ?? [];

        var found = IndexOf(current);
        _index = found >= 0 ? found
               : sameArtist ? Math.Min(_index, _urls.Count - 1)
               : -1;
    }

    public void Clear()
    {
        ArtistKey = null;
        _urls = [];
        _index = -1;
    }

    /// <summary>
    /// The photo one step in <paramref name="direction"/> (+1 forwards, -1 back), or null at the end
    /// of the deck. Does not move; see <see cref="Commit"/>.
    /// </summary>
    /// <remarks>From an unknown position (nothing chosen yet) a step forwards lands on the first photo
    /// and a step back on the last, so the gesture always does something.</remarks>
    public string? Peek(int direction)
    {
        if (_urls.Count == 0 || direction == 0)
            return null;

        var step = Math.Sign(direction);
        var next = _index < 0
            ? (step > 0 ? 0 : _urls.Count - 1)
            : _index + step;

        return next >= 0 && next < _urls.Count ? _urls[next] : null;
    }

    /// <summary>Moves to a photo that is now on screen. A URL that is not in the deck is ignored: the
    /// deck describes what it holds, and it cannot describe a picture from somewhere else.</summary>
    public void Commit(string? url)
    {
        var found = IndexOf(url);
        if (found >= 0)
            _index = found;
    }

    private int IndexOf(string? url)
    {
        if (string.IsNullOrEmpty(url))
            return -1;

        for (var i = 0; i < _urls.Count; i++)
            if (string.Equals(_urls[i], url, StringComparison.OrdinalIgnoreCase))
                return i;

        return -1;
    }
}
