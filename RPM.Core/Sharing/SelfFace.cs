namespace RPM.Core.Sharing;

/// <summary>Why the machine's own profile is being asked for.</summary>
/// <remarks>There is no default. Both doors are named at every call site, because they are not
/// interchangeable. An account that has just changed must be asked about, whatever was tried before. A
/// redraw must not ask again while an answer is in the air.</remarks>
public enum SelfLookup
{
    /// <summary>The account changed. Go and ask, whatever happened last time.</summary>
    Now,

    /// <summary>Something is drawing the face. Ask only if nothing has answered and the wait after the
    /// last failure is up.</summary>
    IfDue,
}

/// <summary>What <see cref="SelfFace.Asked"/> settled.</summary>
/// <param name="Fetch">Go and read the profile. False when one is already in the air, when the answer is in
/// hand, or when the wait after a failure has not run out.</param>
/// <param name="ForgetWrittenName">The name written down belongs to somebody who is no longer at this
/// machine, and must be cleared before it goes out on the wire again.</param>
/// <param name="Changed">The face changed in this call, so whoever draws it has to be told.</param>
public readonly record struct SelfAsk(bool Fetch, bool ForgetWrittenName, bool Changed);

/// <summary>
/// The face of whoever is sitting at THIS machine: whose it is, when it may be asked for again, and what
/// to show while nothing has answered.
///
/// <para>
/// A failed lookup must not be final. If it were, a request that timed out (a cold Lambda is enough)
/// would leave this machine with no owner for the rest of the session. The row would have no name and no
/// picture, although the computer's name is in the settings the whole time. Nothing would ask again,
/// because nothing kept the answer "not yet".
/// </para>
///
/// <para>So the two rules that decide it are here, and they are the only door to that state:
/// <list type="number">
/// <item>A failure is a wait, not a verdict. It sets a time after which the next redraw asks again. This
/// is the same rule that the faces of other machines follow.</item>
/// <item>The name written down stands in meanwhile. It is the same name the beacon announces with no
/// network at all. Having the flyout disagree with the beacon about who is at this computer is worse than
/// being a picture short.</item>
/// </list>
/// </para>
///
/// <para>Not thread-safe, on purpose. It holds no lock of its own. It is called under the one lock its
/// owner already takes around the faces. Two locks over one answer is how the face on screen and the name
/// on the wire end up being read from different moments.</para>
/// </summary>
public sealed class SelfFace
{
    /// <summary>How long a failed lookup is left alone. Long enough that a redraw a second later does not
    /// ask again. Short enough that a network coming back is noticed while the person is still looking at
    /// the screen.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);

    /// <summary>Nothing more to ask: the answer is in hand, one is in the air, or nobody is signed in. A
    /// tick count never reaches it.</summary>
    private const long Never = long.MaxValue;

    private PeerFace _face = PeerFace.Nobody;
    private string? _account;
    private long _next;

    /// <summary>The face as it stands now. Never null. <see cref="PeerFace.Nobody"/> is a perfectly good
    /// answer. A row that is drawing itself cannot wait.</summary>
    public PeerFace Face => _face;

    /// <summary>Nobody is signed in. Returns true when that is news worth redrawing for.</summary>
    /// <remarks>Answered here and now, rather than left to a lookup that would fail. The next person to
    /// sign in on this machine must not inherit the last person's name while a request is in progress.</remarks>
    public bool SignedOut()
    {
        bool news = _face.IsKnown;

        _face = PeerFace.Nobody;
        _account = null;
        _next = Never;

        return news;
    }

    /// <summary>
    /// Somebody is signed in and something wants their face.
    /// </summary>
    /// <param name="user">The account signed in here.</param>
    /// <param name="writtenName">The display name last read for this machine, as the settings hold it, or
    /// null. This is what the beacon is announcing.</param>
    /// <param name="when">Which door this came through.</param>
    /// <param name="now">The tick count, for the wait after a failure.</param>
    public SelfAsk Asked(string user, string? writtenName, SelfLookup when, long now)
    {
        ArgumentException.ThrowIfNullOrEmpty(user);

        bool forget = false;
        bool changed = false;

        if (_account is null)
        {
            // First sight. The name written down was read for whoever was signed in when it was written.
            // Signing out clears the name and the session together. So at startup, the name belongs to the
            // session that was restored beside it, and it is this person's.
            _account = user;
        }
        else if (!string.Equals(_account, user, StringComparison.Ordinal))
        {
            // Somebody else is here now. Nothing learned for the last person is theirs. The name on the
            // wire least of all.
            _account = user;
            _face = PeerFace.Nobody;
            _next = 0;
            writtenName = null;
            forget = true;
            changed = true;
        }

        if (!_face.IsKnown && !string.IsNullOrWhiteSpace(writtenName))
        {
            // A name and no picture. The picture cannot be rebuilt from what is written down. The name is
            // what says whose computer this is.
            _face = new PeerFace(writtenName.Trim(), null);
            changed = true;
        }

        bool fetch = when == SelfLookup.Now || now >= _next;

        // Claimed here, so that two redraws in the same moment do not both go and ask.
        if (fetch)
            _next = Never;

        return new SelfAsk(fetch, forget, changed);
    }

    /// <summary>
    /// The profile came back. False when it is stale: the person signed out, or signed in as somebody else,
    /// while it was in the air. In that case the caller must write nothing.
    /// </summary>
    /// <remarks>Checked against the account it was asked for, rather than trusted, because the two are not
    /// the same moment. Without this check, a machine would keep wearing the face of somebody who has left
    /// it.</remarks>
    public bool Learned(string user, PeerFace face)
    {
        ArgumentNullException.ThrowIfNull(face);

        if (!string.Equals(_account, user, StringComparison.Ordinal))
            return false;

        _face = face;
        _next = Never;
        return true;
    }

    /// <summary>The lookup failed. The face already shown stands. The next redraw after the wait asks
    /// again.</summary>
    public void Failed(string user, long now)
    {
        if (!string.Equals(_account, user, StringComparison.Ordinal))
            return;

        _next = now + (long)RetryAfter.TotalMilliseconds;
    }
}
