using System.Diagnostics;
using RPM.Core.Cloud;
using RPM.Core.Sharing;
using RPM.Desktop.Services.Account;
using RPM.Desktop.Services.Shell;
using RPM.Core.Diagnostics;

namespace RPM.Desktop.Services.Sharing;

/// <summary>Who the shared computers belong to: the names and pictures the share button puts on its
/// rows.
/// <para>A peer is a machine. A machine's name is what it calls itself on the network, for example
/// DESKTOP-4K2, not a person's name. The person is the account it is signed into. That is a fact held
/// by the API, not by the machine. So it has to be fetched, the fetch can fail, and often there is no
/// account at all. This class keeps what has been learned so far and hands it back without ever
/// blocking. A row that is being drawn now gets what is known now, and is drawn again when more is
/// known.</para>
/// <para>Events arrive on their own clocks. Each one is safe for the reason given below.
/// <list type="bullet">
/// <item>The UI thread calls <see cref="Learn"/> from a redraw. This is safe because it only reads its
/// arguments and adds work. It never waits for a fetch, so a redraw costs nothing.</item>
/// <item>A fetch finishes on the thread pool, at any time after that redraw. This is safe because it
/// writes one entry under the lock and then raises <see cref="Changed"/>. Nothing about it depends on
/// which redraw asked for it.</item>
/// <item><see cref="Changed"/> calls back into <see cref="Learn"/> through the redraw it causes. This
/// is safe because each user id is asked about once. The second pass finds the id already answered and
/// adds no work, so the loop stops on its second turn.</item>
/// <item>A peer is forgotten while its fetch is still running. This is safe because the answer is
/// stored under the account, not the peer. A row that nobody builds is a row that nobody reads.</item>
/// <item>The account changes, from a sign-in or a sign-out, into <see cref="LearnSelf"/>. This is safe
/// because a sign-out is answered with no fetch. The name is cleared at once, so the state is correct
/// before anything else can ask.</item>
/// <item>This machine's own profile arrives after that. It sets two things: the name the beacon
/// announces, and <see cref="Self"/>, the face that the share button's own row shows. This is safe only
/// because the fetch carries the account it was asked for, and <see cref="SelfFace.Learned"/> compares
/// that account before it writes. If the user signs out while a fetch is running, the answer belongs to
/// nobody. Without that comparison, the beacon would keep naming a person who has left, and the flyout
/// would keep showing their picture.</item>
/// <item>A redraw asks for the own face again through <see cref="SelfLookup.IfDue"/> while an earlier
/// lookup is running or has just failed. This is safe because <see cref="SelfFace"/> holds one answer
/// to "may this be asked now", and it claims that answer under the same lock as the faces. A lookup in
/// flight, or one waiting after a failure, says no. So redraws add no work, and the loop that a
/// <see cref="Changed"/> redraw would otherwise cause stops on its second turn.</item>
/// <item>The library band and the share button both read a name during a redraw while any of the above
/// is running. This is safe because both only read what is known now, and <see cref="Changed"/> brings
/// them back. Neither keeps a name from one redraw to the next.</item>
/// </list>
/// </para>
/// </summary>
public sealed class PeerFaces
{
    private readonly RpmCommunityClient? _community;
    private readonly SettingsService? _settings;
    private readonly AuthService? _auth;

    public PeerFaces(RpmCommunityClient? community, SettingsService? settings, AuthService? auth)
    {
        _community = community;
        _settings = settings;
        _auth = auth;

        if (_auth is not null)
            _auth.Changed += OnAccountChanged;

        LearnSelf(SelfLookup.Now);
    }

    /// <summary>How long a failed lookup waits before it is tried again. The usual reason for a failure
    /// is that this machine is offline. That state lasts minutes, and redraws happen far more
    /// often.</summary>
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();

    /// <summary>Which account each computer is signed into, as its peer record says.</summary>
    private readonly Dictionary<string, string> _accounts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The faces that have been fetched, by account.</summary>
    private readonly Dictionary<string, PeerFace> _faces = new(StringComparer.Ordinal);

    /// <summary>The earliest time an account may be asked about again. <see cref="long.MaxValue"/> marks
    /// an account that has already answered. Its name and picture may then be a whole session
    /// old.</summary>
    private readonly Dictionary<string, long> _next = new(StringComparer.Ordinal);

    /// <summary>The person signed in on this machine: the face, whose it is, and when it may be asked
    /// for again. It is guarded by the same lock as the peers' faces, because redraws read it and fetches
    /// write it. See <see cref="SelfFace"/>, which takes no lock of its own.</summary>
    private readonly SelfFace _self = new();

    /// <summary>Raised from the thread pool when a face has been learned. A listener is drawing
    /// something, so it must move back to the UI thread first.</summary>
    public event Action? Changed;

    /// <summary>
    /// The account signed in on this machine: the name it goes by and the picture set for it in the
    /// settings. It is <see cref="PeerFace.Nobody"/> as soon as somebody signs out. Until a name is known,
    /// the name that was written down is used. Once the lookup answers, the fetched name is used.
    ///
    /// <para>This comes from the same fetch that the beacon's name comes from, and it is not looked up
    /// again. The share button's own row shows this picture. A second path to the same answer would let
    /// the flyout and the beacon disagree about who is using this machine.</para>
    /// </summary>
    public PeerFace Self
    {
        get
        {
            lock (_gate)
                return _self.Face;
        }
    }

    /// <summary>
    /// The person behind a computer, as far as anything is known right now. <see cref="PeerFace.Nobody"/>
    /// when they are signed out, have no profile, or have simply not been looked up yet.
    /// </summary>
    public PeerFace Face(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
            return PeerFace.Nobody;

        lock (_gate)
            return _accounts.TryGetValue(deviceId, out var user)
                   && _faces.TryGetValue(user, out var face)
                ? face
                : PeerFace.Nobody;
    }

    /// <summary>
    /// What to call a computer, in one call: the profile if one has been learned, and the two things
    /// it broadcast about itself if not. <see cref="PeerLabel.For"/> is the rule; this is the join
    /// between it and what has been fetched so far.
    /// </summary>
    /// <param name="deviceId">Which machine.</param>
    /// <param name="broadcastFace">The display name off its beacon, or empty.</param>
    /// <param name="machineName">What it calls itself. The answer when nothing else is known.</param>
    public string Label(string deviceId, string broadcastFace, string machineName) =>
        PeerLabel.For(Face(deviceId), broadcastFace, machineName);

    /// <summary>
    /// Takes in the peers as they stand and starts whatever looking-up they call for. Returns
    /// immediately: what is already known is available to <see cref="Face"/> the moment this is
    /// called, and what is not raises <see cref="Changed"/> when it lands.
    /// </summary>
    public void Learn(IEnumerable<TrustedPeer> peers)
    {
        ArgumentNullException.ThrowIfNull(peers);

        if (_community is null)
            return;

        List<string>? fetch = null;
        long now = Environment.TickCount64;

        lock (_gate)
        {
            foreach (var peer in peers)
            {
                if (peer.AccountUserId is not { Length: > 0 } user)
                    continue;

                _accounts[peer.DeviceId] = user;

                if (_next.TryGetValue(user, out long due) && now < due)
                    continue;

                // Claimed here, inside the lock, so two redraws in the same moment do not both ask.
                _next[user] = long.MaxValue;
                (fetch ??= []).Add(user);
            }
        }

        if (fetch is null)
            return;

        foreach (var user in fetch)
            Shell.Background.Run(() => FetchAsync(user));
    }

    /// <summary>
    /// This machine's own display name, written where the beacon can reach it with no network.
    ///
    /// <para>
    /// The name is kept here, beside the other lookups, because it is a fact the API holds, not one the
    /// machine holds. It is the same fact that the peers are looked up against. Fetching it anywhere else
    /// would give two paths to one answer. The account API also has a display name from sign-in. That is
    /// a different name in a different store. Broadcasting that one would put a name on the wire that no
    /// peer lookup agrees with, and the label would change under the peers as soon as the profile arrived.
    /// </para>
    ///
    /// <para>Writing the name is what makes it travel. The settings raise their own change. The sharing
    /// service asks <see cref="RPM.Core.Sharing.BeaconReaction"/> whether that change is worth anything,
    /// and a face that actually moved is the one thing that causes a re-announcement.</para>
    ///
    /// <para>This is safe to call as often as anything draws the face. What may be asked, and when, is
    /// decided by <see cref="SelfFace"/>, not by this method. Only one lookup runs at a time, there is one
    /// wait after a failure, and the written-down name is shown in the meantime.</para>
    /// </summary>
    /// <param name="when">Which door. <see cref="SelfLookup.Now"/> for an account that just
    /// changed; <see cref="SelfLookup.IfDue"/> for a redraw.</param>
    public void LearnSelf(SelfLookup when)
    {
        if (_settings is null)
            return;

        // A signed-out machine has an answer, and it is immediate: nobody. It is not left to a fetch,
        // which would fail. The next person to sign in on this machine must not inherit the last
        // person's name.
        if (_auth is null || !_auth.IsLoggedIn || _community is null)
        {
            _settings.ProfileName = null;

            bool news;
            lock (_gate)
                news = _self.SignedOut();

            // Raise the change only when it moved. This runs from a redraw, and raising it on every
            // call would cause another redraw each time.
            if (news)
                Changed?.Invoke();

            return;
        }

        var user = _auth.UserId!;

        SelfAsk ask;
        lock (_gate)
            ask = _self.Asked(user, _settings.ProfileName, when, Environment.TickCount64);

        // A different person is signed in now, so the name on the wire belongs to the wrong person.
        if (ask.ForgetWrittenName)
            _settings.ProfileName = null;

        if (ask.Changed)
            Changed?.Invoke();

        if (ask.Fetch)
            Shell.Background.Run(() => FetchSelfAsync(user));
    }

    private void OnAccountChanged() => LearnSelf(SelfLookup.Now);

    /// <param name="asked">Who was signed in when this was asked. It is passed on so that
    /// <see cref="SelfFace.Learned"/> can check it against who is signed in when the answer lands. These
    /// are different moments. If the user signs out in between, the arriving name would be written over
    /// the null that signing out just wrote. The machine would then broadcast a person who is no longer
    /// signed in.</param>
    private async Task FetchSelfAsync(string asked)
    {
        try
        {
            var profile = await _community!.GetProfileAsync();

            // An account with a name or a picture is somebody. Only an account with neither is nobody.
            // An account that has a picture but no name is still a face, because the own row then
            // draws only the picture.
            var face = profile.DisplayName.Length == 0 && !profile.HasAvatar
                ? PeerFace.Nobody
                : new PeerFace(profile.DisplayName, Picture(profile), profile.Supporter);

            bool mine;
            lock (_gate)
                mine = _self.Learned(asked, face);

            if (!mine)
                return;

            _settings!.ProfileName = profile.DisplayName;
        }
        catch (Exception ex)
        {
            // The machine may be offline, or the API may be slow. For example, a cold Lambda can take
            // longer than the client waits. The name already written down stands. It is the name this
            // machine is announcing, and a failed lookup does not mean that it changed. The next redraw
            // after the wait asks again. If this were recorded as a final answer, one slow request at
            // startup would leave the computer with no owner until the app was restarted.
            Said.Line("sharing", $"Could not read who this machine belongs to: {ex.Message}");

            lock (_gate)
                _self.Failed(asked, Environment.TickCount64);

            return;
        }

        Changed?.Invoke();
    }

    /// <summary>Where a profile's picture is, or null when it has none. It is in one place, because the
    /// own row and a peer's row build the same address from the same field.</summary>
    private Uri? Picture(RpmProfile profile) =>
        profile.HasAvatar && _community!.BaseAddress is { } api
            ? new Uri(api, RpmCommunityClient.AvatarPath(profile.UserId, profile.AvatarUpdatedAt))
            : null;

    private async Task FetchAsync(string user)
    {
        try
        {
            var profile = await _community!.GetPublicProfileAsync(user);

            // No profile is an answer, and it is stored as one. An account that has never set a name
            // will not set one just because the flyout was opened again.
            var face = profile is null || profile.DisplayName.Length == 0
                ? PeerFace.Nobody
                : new PeerFace(profile.DisplayName, Picture(profile), profile.Supporter);

            lock (_gate)
                _faces[user] = face;
        }
        catch (Exception ex)
        {
            // The machine may be offline, or the API may be slow. Neither is worth showing on a share
            // button. The machine's own name is a good enough name for a computer.
            Said.Line("sharing", $"Could not read an account's profile: {ex.Message}");

            lock (_gate)
                _next[user] = Environment.TickCount64 + (long)RetryAfter.TotalMilliseconds;

            return;
        }

        Changed?.Invoke();
    }
}
