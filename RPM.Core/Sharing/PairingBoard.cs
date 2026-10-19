using System.Security.Cryptography;

namespace RPM.Core.Sharing;

/// <summary>Which side of a pairing this computer is on.</summary>
public enum PairingDirection
{
    /// <summary>This computer asked. It has the box that the code is typed into.</summary>
    Asking,

    /// <summary>The other computer asked. This computer shows the code and checks it.</summary>
    Asked,
}

/// <summary>Where one pairing stands. The first four states are live. The rest are endings.</summary>
public enum PairingState
{
    /// <summary>Asking side: the request is on its way.</summary>
    Sending,

    /// <summary>Asking side: the other computer answered 409. It is asking this one, and its request has not
    /// arrived yet.</summary>
    CrossedWaiting,

    /// <summary>Asking side: the code is on the other screen and this one has the box.</summary>
    WaitingForCode,

    /// <summary>Asked side: this screen shows the code.</summary>
    ShowingCode,

    Accepted,
    Declined,
    Expired,
    TooManyTries,

    /// <summary>Asked side: the asking computer cancelled.</summary>
    Withdrawn,

    /// <summary>The other computer stopped answering, or stopped following the request.</summary>
    Lost,

    /// <summary>One of the two computers already shares with five.</summary>
    Full,

    /// <summary>Asking side: the request could not be delivered.</summary>
    Unreachable,

    /// <summary>Asking side: the other computer takes no requests for a while, after too many wrong codes.</summary>
    Resting,

    /// <summary>Asking side: the other computer runs a build that cannot pair the way this one does
    /// (<see cref="SharingProtocol.Pairing"/>).</summary>
    NeedsUpdateThere,
}

public static class PairingStateFacts
{
    public static bool IsLive(this PairingState state) =>
        state is PairingState.Sending or PairingState.CrossedWaiting
              or PairingState.WaitingForCode or PairingState.ShowingCode;
}

/// <summary>
/// Every duration and count that the pairing flow uses, in one place. Tests pass short values. The app
/// uses <see cref="Standard"/>.
/// </summary>
/// <param name="Life">How long a request stands before it expires.</param>
/// <param name="Tries">How many wrong codes end a pairing.</param>
/// <param name="Hold">How long the asked side holds a status call open when nothing changes.</param>
/// <param name="Lease">How long the asked side waits, with no status call open, before it decides that the
/// asking computer has gone.</param>
/// <param name="LostAfter">How long the asking side keeps retrying failed status calls before it decides that
/// the asked computer has gone.</param>
/// <param name="CrossedPatience">How long the asking side waits for the other computer's request after a 409.</param>
/// <param name="Keep">How long the asked side keeps an ended pairing readable over the wire after it has been
/// dismissed.</param>
/// <param name="WrongCodesAllowed">How many wrong codes, across every request, within
/// <paramref name="WrongCodeWindow"/>, make the asked side stop taking requests for <paramref name="RestFor"/>.
/// Each request allows <paramref name="Tries"/> guesses. Without this limit a computer could keep asking. Every
/// request puts a question on the owner's screen, so the number of requests must be bounded.</param>
/// <param name="WrongCodeWindow">How far back wrong codes are counted.</param>
/// <param name="RestFor">How long the asked side takes no requests once the count is reached.</param>
public sealed record PairingTimes(
    TimeSpan Life, int Tries, TimeSpan Hold, TimeSpan Lease, TimeSpan LostAfter,
    TimeSpan CrossedPatience, TimeSpan Keep,
    int WrongCodesAllowed, TimeSpan WrongCodeWindow, TimeSpan RestFor)
{
    public static PairingTimes Standard { get; } = new(
        Life: TimeSpan.FromMinutes(2),
        Tries: 3,
        Hold: TimeSpan.FromSeconds(10),
        Lease: TimeSpan.FromSeconds(10),
        LostAfter: TimeSpan.FromSeconds(15),
        CrossedPatience: TimeSpan.FromSeconds(10),
        Keep: TimeSpan.FromMinutes(1),
        WrongCodesAllowed: 10,
        WrongCodeWindow: TimeSpan.FromMinutes(10),
        RestFor: TimeSpan.FromMinutes(10));
}

/// <summary>Who the other computer says it is: what a pairing is about, and what is saved as the pairing record
/// when it is accepted.</summary>
public sealed record PairingPeer(string DeviceId, string Name, string Face, byte[] PublicKey, string Account);

/// <summary>One pairing, as it stood when it was read. It cannot be changed. Screens read these and never keep
/// state of their own about a pairing.</summary>
public sealed record Pairing
{
    public required PairingPeer Peer { get; init; }
    public required PairingDirection Direction { get; init; }

    /// <summary>A local counter, new for every Ask and every incoming request. A reply for an attempt that is no
    /// longer the current one is ignored.</summary>
    public required long Attempt { get; init; }

    /// <summary>The pairing id on the wire. Empty on the asking side until the offer arrives.</summary>
    public string Id { get; init; } = "";

    public required PairingState State { get; init; }

    /// <summary>The six digits. Asked side only.</summary>
    public string Code { get; init; } = "";

    public int TriesLeft { get; init; }

    /// <summary>The last code typed was wrong, and the pairing is still live.</summary>
    public bool WrongCode { get; init; }

    public DateTime ExpiresAtUtc { get; init; }

    /// <summary>Increases on every change. What a held status call waits on.</summary>
    public long Rev { get; init; }

    /// <summary>The row is hidden. The pairing itself stays readable; see
    /// <see cref="PairingBoard.Dismiss"/>.</summary>
    public bool Dismissed { get; init; }

    public string DeviceId => Peer.DeviceId;
    public bool IsLive => State.IsLive();
}

/// <summary>
/// Every pairing this computer is part of, at most one per other computer, and the only place a
/// pairing changes.
///
/// <para>Shared by the server (requests other computers make of this one) and the service (requests
/// this one makes). Both directions go through one lock, which is what settles two computers asking
/// each other at the same moment. Screens read <see cref="Snapshot"/> whenever
/// <see cref="Changed"/> is raised.</para>
///
/// <para>Rules, each one tested in <c>PairingBoardTests</c>:</para>
/// <list type="bullet">
/// <item>The first ending wins. An ended pairing's state does not change again.</item>
/// <item>Expiry is checked on every read, not only by <see cref="Sweep"/>.</item>
/// <item>Every change from the network names its pairing: by id on the asked side, by attempt on
/// the asking side. A reply for a replaced attempt changes nothing.</item>
/// <item>An asked pairing stays readable by id after it ends, for <see cref="PairingTimes.Keep"/>
/// after it is dismissed, so the asking computer reads the real ending instead of
/// <see cref="PairStatusKind.Gone"/>.</item>
/// </list>
/// </summary>
public sealed class PairingBoard
{
    private readonly Lock _gate = new();
    private readonly PairingTimes _times;
    private readonly Func<DateTime> _now;

    /// <summary>The current pairing for each other computer, by device id. The screens and the crossed-request
    /// rule read this.</summary>
    private readonly Dictionary<string, Entry> _slots = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Asked pairings by wire id. The status, code and withdraw routes read this. An entry can stay here
    /// after its slot has been taken by a newer pairing.</summary>
    private readonly Dictionary<string, Entry> _asked = new(StringComparer.Ordinal);

    private long _attempts;

    /// <summary>When each recent wrong code was answered, on the asked side, across every request. The rest that
    /// follows too many wrong codes is decided from this.</summary>
    private readonly List<DateTime> _wrongCodes = [];

    /// <summary>The time until which no request is taken, after <see cref="PairingTimes.WrongCodesAllowed"/> wrong
    /// codes within <see cref="PairingTimes.WrongCodeWindow"/>.</summary>
    private DateTime _restingUntil = DateTime.MinValue;

    /// <param name="times">Durations and counts. The app uses <see cref="PairingTimes.Standard"/>.</param>
    /// <param name="clock">The current UTC time. Tests pass a clock that they move by hand.</param>
    public PairingBoard(PairingTimes times, Func<DateTime> clock)
    {
        _times = times ?? throw new ArgumentNullException(nameof(times));
        _now = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public PairingTimes Times => _times;

    /// <summary>Raised after any pairing changes. It is raised outside the lock, on whichever thread made the
    /// change.</summary>
    public event Action? Changed;

    /// <summary>Every pairing with a slot, including ended and dismissed ones.</summary>
    public IReadOnlyList<Pairing> Snapshot()
    {
        lock (_gate)
            return [.. _slots.Values.Select(e => e.Freeze())];
    }

    /// <summary>The current pairing with one computer, or null.</summary>
    public Pairing? Find(string deviceId)
    {
        lock (_gate)
            return _slots.TryGetValue(deviceId, out var entry) ? entry.Freeze() : null;
    }

    /// <summary>Whether something on the board is waiting for the person here: a code to read out, or an ending
    /// that nobody has dismissed. An acceptance does not count, because it needs nothing from the person.</summary>
    public bool NeedsAttention
    {
        get
        {
            lock (_gate)
            {
                return _slots.Values.Any(e =>
                    !e.Dismissed &&
                    (e.State is PairingState.ShowingCode ||
                     (!e.State.IsLive() && e.State is not PairingState.Accepted)));
            }
        }
    }

    // ── asking side ──

    /// <summary>
    /// Starts asking a computer. The pairing is registered before anything is sent. That is what makes the
    /// crossed-request rule in <see cref="Incoming"/> hold.
    /// </summary>
    /// <returns>The new pairing and true, or the live pairing that already stands with that computer, and false.
    /// A live pairing in either direction is never replaced from here.</returns>
    public (Pairing Pairing, bool Started) Begin(PairingPeer peer)
    {
        ArgumentNullException.ThrowIfNull(peer);

        Pairing result;
        lock (_gate)
        {
            if (_slots.TryGetValue(peer.DeviceId, out var standing) && standing.State.IsLive())
                return (standing.Freeze(), false);

            var entry = new Entry(peer, PairingDirection.Asking, ++_attempts, PairingState.Sending)
            {
                TriesLeft = _times.Tries,
            };
            _slots[peer.DeviceId] = entry;
            result = entry.Freeze();
        }

        Changed?.Invoke();
        return (result, true);
    }

    /// <summary>The offer arrived: the code is on the other screen.</summary>
    public void Offered(string deviceId, long attempt, string id, PairStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        Apply(deviceId, attempt, entry =>
        {
            entry.Id = id ?? "";
            return Mirror(entry, status);
        });
    }

    /// <summary>The other computer answered 409: it is asking this one. Its request replaces this pairing when it
    /// arrives. If that request has already arrived, this attempt is gone and nothing happens.</summary>
    public void Crossed(string deviceId, long attempt) => Apply(deviceId, attempt, entry =>
    {
        if (entry.State is not PairingState.Sending)
            return false;

        entry.State = PairingState.CrossedWaiting;
        entry.CrossedSinceUtc = _now();
        return true;
    });

    /// <summary>The request could not be delivered.</summary>
    public void Undelivered(string deviceId, long attempt) =>
        Apply(deviceId, attempt, entry => End(entry, PairingState.Unreachable));

    /// <summary>Status calls have failed for <see cref="PairingTimes.LostAfter"/>.</summary>
    public void Lose(string deviceId, long attempt) =>
        Apply(deviceId, attempt, entry => End(entry, PairingState.Lost));

    /// <summary>
    /// A status or code reply from the asked computer, copied onto this side's pairing.
    /// </summary>
    /// <returns>The pairing when THIS call moved it to <see cref="PairingState.Accepted"/>, and null otherwise.
    /// The caller that gets it records the other computer. Every other caller does not, so the recording happens
    /// once, however many replies say Accepted.</returns>
    public Pairing? Mirror(string deviceId, long attempt, PairStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        Pairing? accepted = null;
        Apply(deviceId, attempt, entry =>
        {
            bool changed = Mirror(entry, status);
            if (changed && entry.State is PairingState.Accepted)
                accepted = entry.Freeze();
            return changed;
        });
        return accepted;
    }

    /// <summary>The other computer answered with an offer that has no commitment. That is a build that cannot
    /// pair the way this one does (<see cref="SharingProtocol.Pairing"/>).</summary>
    public void Outdated(string deviceId, long attempt) =>
        Apply(deviceId, attempt, entry => End(entry, PairingState.NeedsUpdateThere));

    /// <summary>Whether this attempt is still the current, live pairing with that computer. The
    /// asking side's follower stops when it is not.</summary>
    public bool IsCurrent(string deviceId, long attempt)
    {
        lock (_gate)
        {
            return _slots.TryGetValue(deviceId, out var entry)
                && entry.Direction is PairingDirection.Asking
                && entry.Attempt == attempt
                && entry.State.IsLive();
        }
    }

    /// <summary>
    /// The person here pressed Cancel. The pairing is removed from this board, and the caller sends the
    /// withdraw using the id that this returns.
    /// </summary>
    /// <returns>The pairing that was removed, or null when there was no live request to cancel.</returns>
    public Pairing? Withdraw(string deviceId)
    {
        Pairing removed;
        lock (_gate)
        {
            if (!_slots.TryGetValue(deviceId, out var entry) ||
                entry.Direction is not PairingDirection.Asking || !entry.State.IsLive())
                return null;

            removed = entry.Freeze();
            _slots.Remove(deviceId);
            entry.Signal();
        }

        Changed?.Invoke();
        return removed;
    }

    // ── asked side ──

    /// <summary>What happened to an incoming request.</summary>
    public enum IncomingKind
    {
        /// <summary>A new pairing shows the code.</summary>
        Created,

        /// <summary>This computer is asking that one and asks first: answer 409.</summary>
        Crossed,

        /// <summary>This computer already shares with five.</summary>
        Full,

        /// <summary>This computer takes no requests for a while, after too many wrong codes.</summary>
        Resting,
    }

    /// <summary>
    /// Another computer asks this one.
    /// </summary>
    /// <param name="myDeviceId">This computer's device id, for the crossed-request rule.</param>
    /// <param name="peer">Who is asking, as the request says.</param>
    /// <param name="secrets">Both keys. Each round is made and checked from them. The digits go on screen once
    /// the asking computer sends its nonce for the round (<see cref="Join"/>).</param>
    /// <param name="roomHere">Whether this computer can add one more.</param>
    public (IncomingKind Kind, Pairing? Pairing) Incoming(
        string myDeviceId, PairingPeer peer, PairingSecrets secrets, bool roomHere)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(secrets);

        Pairing created;
        lock (_gate)
        {
            // Checked before anything reaches the screen. A computer resting after too many wrong codes shows
            // nobody a question.
            if (_now() < _restingUntil)
                return (IncomingKind.Resting, null);

            if (_slots.TryGetValue(peer.DeviceId, out var standing) && standing.State.IsLive())
            {
                if (standing.Direction is PairingDirection.Asking)
                {
                    // Both computers have asked. The smaller id asks, and the other one's request is answered with 409.
                    // Its own request is already on its way here.
                    if (PairingCode.AsksFirst(myDeviceId, peer.DeviceId))
                        return (IncomingKind.Crossed, null);

                    // This computer stands down. Its row turns into the question in the same step, so there is never a
                    // moment with nothing on the screen.
                    standing.Signal();
                }
                else
                {
                    // The same computer is asking again, for example after a restart. The new request replaces the old one.
                    End(standing, PairingState.Withdrawn);
                }
            }

            if (!roomHere)
                return (IncomingKind.Full, null);

            var now = _now();
            var round = secrets.Round(1);
            var entry = new Entry(peer, PairingDirection.Asked, ++_attempts, PairingState.ShowingCode)
            {
                Id = NewId(),
                Secrets = secrets,
                Round = round,
                TriesLeft = _times.Tries,
                ExpiresAtUtc = now + _times.Life,
                LastFollowEndedUtc = now,
            };

            _slots[peer.DeviceId] = entry;
            _asked[entry.Id] = entry;
            created = entry.Freeze();
        }

        Changed?.Invoke();
        return (IncomingKind.Created, created);
    }

    /// <summary>The wire status of an asked pairing, or null when this computer has no pairing with that id.</summary>
    public PairStatus? Status(string id)
    {
        bool changed;
        PairStatus? status;
        lock (_gate)
        {
            if (!_asked.TryGetValue(id ?? "", out var entry))
                return null;

            changed = Settle(entry);
            status = entry.Status(_now());
        }

        if (changed)
            Changed?.Invoke();
        return status;
    }

    /// <summary>
    /// A held status call. It answers when the pairing's revision is no longer <paramref name="rev"/>, when the
    /// pairing has ended, or after <see cref="PairingTimes.Hold"/>.
    ///
    /// <para>While it is held, the asking computer counts as following, and the lease does not run. The lease
    /// starts when the last held call ends.</para>
    /// </summary>
    /// <returns>The status, or null for an id this computer does not have.</returns>
    public async Task<PairStatus?> WaitAsync(string id, long rev, CancellationToken cancellation)
    {
        Entry entry;
        Task signal;
        lock (_gate)
        {
            if (!_asked.TryGetValue(id ?? "", out var found))
                return null;

            entry = found;
            entry.Followers++;
            signal = entry.Changed.Task;
        }

        try
        {
            var status = Status(id!);
            if (status is null || status.Rev != rev || status.State is not PairStatusKind.ShowingCode)
                return status;

            await Task.WhenAny(signal, Task.Delay(_times.Hold, cancellation));
            cancellation.ThrowIfCancellationRequested();

            return Status(id!);
        }
        finally
        {
            lock (_gate)
            {
                entry.Followers--;
                entry.LastFollowEndedUtc = _now();
            }
        }
    }

    /// <summary>
    /// The asking computer's nonce for one round, which puts the digits on this screen. The first one for the
    /// round is kept, and any other is ignored. The asking computer derives
    /// it, so a second send for the same round carries the same bytes.
    /// </summary>
    /// <returns>The status, or null for an unknown id.</returns>
    public PairStatus? Join(string id, int round, byte[] askerNonce)
    {
        bool changed;
        PairStatus status;
        lock (_gate)
        {
            if (!_asked.TryGetValue(id ?? "", out var entry))
                return null;

            changed = Settle(entry);
            if (entry.State.IsLive() && entry.Round is { AskerNonce: null } current && current.Number == round &&
                entry.Secrets is { } secrets && askerNonce is { Length: > 0 })
            {
                entry.Round = secrets.Joined(current, askerNonce);
                entry.Code = entry.Round.Code;
                Touch(entry);
                changed = true;
            }

            status = entry.Status(_now());
        }

        if (changed)
            Changed?.Invoke();
        return status;
    }

    /// <summary>
    /// The asking computer's commitment to the code typed there, for one round. It is taken once per round, and
    /// only once the digits are on this screen. After it is taken, the status reveals this round's nonce.
    ///
    /// <para>The order is the whole point. The nonce is revealed only to a commitment that already exists, so
    /// whoever committed did so without knowing the digits from anywhere except this screen. A second commitment
    /// for the same round is not taken. Otherwise the reveal would let it be made with the right digits.</para>
    /// </summary>
    /// <returns>The status, or null for an unknown id.</returns>
    public PairStatus? Commit(string id, int round, byte[] commitment)
    {
        bool changed;
        PairStatus status;
        lock (_gate)
        {
            if (!_asked.TryGetValue(id ?? "", out var entry))
                return null;

            changed = Settle(entry);
            if (entry.State.IsLive() && entry.Round is { Code.Length: > 0 } current && current.Number == round &&
                entry.AskerCommitment is null && commitment is { Length: > 0 })
            {
                entry.AskerCommitment = commitment;
                Touch(entry);
                changed = true;
            }

            status = entry.Status(_now());
        }

        if (changed)
            Changed?.Invoke();
        return status;
    }

    /// <summary>
    /// The asking computer opens its commitment for one round. This is the whole of this side's check. The
    /// opening has to make the commitment out of both keys and the digits this screen showed. An empty opening
    /// means the asking computer says that the revealed nonce did not give it the code its user typed. That is
    /// a mistyped code, or a machine in the middle, and it counts as a wrong code too.
    ///
    /// <para>A wrong code starts the next round with a new nonce. Its digits go on this screen once the asking
    /// computer sends its nonce for that round. So a nonce that has been revealed never decides anything again.</para>
    /// </summary>
    /// <param name="roomHere">Whether this computer can still add one more. It is checked again here, because a
    /// place can be taken while the code is being checked.</param>
    /// <returns>The status (null for an unknown id), and the pairing when this call accepted it. The caller that
    /// gets the pairing records the other computer.</returns>
    public (PairStatus? Status, Pairing? Accepted) Open(string id, int round, byte[] opening, bool roomHere)
    {
        bool changed;
        PairStatus status;
        Pairing? accepted = null;
        lock (_gate)
        {
            if (!_asked.TryGetValue(id ?? "", out var entry))
                return (null, null);

            changed = Settle(entry);
            if (entry.State.IsLive() && entry.Round is { } current && current.Number == round &&
                entry.AskerCommitment is { } commitment && entry.Secrets is { } secrets)
            {
                changed = true;
                if (secrets.Opens(current, commitment, opening ?? []))
                {
                    End(entry, roomHere ? PairingState.Accepted : PairingState.Full);
                    if (entry.State is PairingState.Accepted)
                        accepted = entry.Freeze();
                }
                else
                {
                    NoteWrongCode();
                    if (--entry.TriesLeft <= 0)
                    {
                        End(entry, PairingState.TooManyTries);
                    }
                    else
                    {
                        entry.Round = secrets.Round(current.Number + 1);
                        entry.Code = "";
                        entry.AskerCommitment = null;
                        entry.WrongCode = true;
                        Touch(entry);
                    }
                }
            }

            status = entry.Status(_now());
        }

        if (changed)
            Changed?.Invoke();
        return (status, accepted);
    }

    /// <summary>Counts one wrong code toward the total across requests. The caller holds the lock.</summary>
    private void NoteWrongCode()
    {
        var now = _now();
        _wrongCodes.RemoveAll(at => now - at >= _times.WrongCodeWindow);
        _wrongCodes.Add(now);

        if (_wrongCodes.Count < _times.WrongCodesAllowed)
            return;

        _restingUntil = now + _times.RestFor;
        _wrongCodes.Clear();
    }

    /// <summary>The asking computer cancelled.</summary>
    public void WithdrawIncoming(string id)
    {
        bool changed;
        lock (_gate)
        {
            if (!_asked.TryGetValue(id ?? "", out var entry))
                return;
            changed = Settle(entry) | End(entry, PairingState.Withdrawn);
        }

        if (changed)
            Changed?.Invoke();
    }

    /// <summary>The person here pressed Decline. The ending is already dismissed, because nobody needs to be told
    /// about their own answer.</summary>
    public void Decline(string deviceId)
    {
        bool changed;
        lock (_gate)
        {
            if (!_slots.TryGetValue(deviceId, out var entry) || entry.Direction is not PairingDirection.Asked)
                return;

            changed = Settle(entry) | End(entry, PairingState.Declined);
            if (entry.State is PairingState.Declined && !entry.Dismissed)
            {
                entry.Dismissed = true;
                entry.DismissedAtUtc = _now();
                Touch(entry);
                changed = true;
            }
        }

        if (changed)
            Changed?.Invoke();
    }

    /// <summary>The server stopped. A code that can no longer be typed does not stay on screen.</summary>
    public void EndAsked()
    {
        bool changed = false;
        lock (_gate)
        {
            foreach (var entry in _asked.Values)
                changed |= End(entry, PairingState.Lost);
        }

        if (changed)
            Changed?.Invoke();
    }

    // ── both ──

    /// <summary>
    /// The person here dismissed an ending. On the asking side the pairing is removed. On the asked side it is
    /// hidden, and it stays readable over the wire until <see cref="Sweep"/> removes it. A live pairing is not
    /// dismissed. It is cancelled or declined.
    /// </summary>
    public void Dismiss(string deviceId)
    {
        lock (_gate)
        {
            if (!_slots.TryGetValue(deviceId, out var entry) || entry.State.IsLive())
                return;

            if (entry.Direction is PairingDirection.Asking)
                _slots.Remove(deviceId);
            else if (!entry.Dismissed)
            {
                entry.Dismissed = true;
                entry.DismissedAtUtc = _now();
                Touch(entry);
            }
            else
                return;
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Ends whatever time has ended: expired requests, requests that nobody follows any more, crossed requests
    /// whose other half never came, and dismissed asked pairings once they have been readable for
    /// <see cref="PairingTimes.Keep"/>. The app runs this once a second.
    /// </summary>
    public void Sweep()
    {
        bool changed = false;
        lock (_gate)
        {
            var now = _now();

            foreach (var entry in _slots.Values.Concat(_asked.Values).Distinct())
            {
                changed |= Settle(entry);

                if (entry.Direction is PairingDirection.Asked && entry.State.IsLive() &&
                    entry.Followers == 0 && now - entry.LastFollowEndedUtc >= _times.Lease)
                {
                    changed |= End(entry, PairingState.Lost);
                }

                if (entry.State is PairingState.CrossedWaiting &&
                    now - entry.CrossedSinceUtc >= _times.CrossedPatience)
                {
                    changed |= End(entry, PairingState.Unreachable);
                }
            }

            foreach (var (id, entry) in _asked.ToList())
            {
                bool current = _slots.TryGetValue(entry.Peer.DeviceId, out var slot) && ReferenceEquals(slot, entry);
                bool readLongEnough = !entry.State.IsLive() && now - entry.EndedAtUtc >= _times.Keep;

                // Taken off the wire once nobody can still be waiting to read it. That is when it ended long enough
                // ago, and it was either replaced or dismissed long enough ago.
                if (readLongEnough && (!current || (entry.Dismissed && now - entry.DismissedAtUtc >= _times.Keep)))
                    _asked.Remove(id);

                if (current && entry.Dismissed && readLongEnough && now - entry.DismissedAtUtc >= _times.Keep)
                {
                    _slots.Remove(entry.Peer.DeviceId);
                    changed = true;
                }
            }
        }

        if (changed)
            Changed?.Invoke();
    }

    // ── inside ──

    /// <summary>Runs a change on the current asking pairing with that computer, if the attempt is still the current
    /// one. A reply for a replaced attempt lands here and changes nothing.</summary>
    private void Apply(string deviceId, long attempt, Func<Entry, bool> change)
    {
        bool changed;
        lock (_gate)
        {
            if (!_slots.TryGetValue(deviceId, out var entry) ||
                entry.Direction is not PairingDirection.Asking ||
                entry.Attempt != attempt || !entry.State.IsLive())
                return;

            changed = change(entry);
        }

        if (changed)
            Changed?.Invoke();
    }

    /// <summary>Copies a wire status onto an asking pairing. Caller holds the lock.</summary>
    private bool Mirror(Entry entry, PairStatus status)
    {
        var state = status.State switch
        {
            PairStatusKind.ShowingCode => PairingState.WaitingForCode,
            PairStatusKind.Accepted => PairingState.Accepted,
            PairStatusKind.Declined => PairingState.Declined,
            PairStatusKind.Expired => PairingState.Expired,
            PairStatusKind.TooManyTries => PairingState.TooManyTries,
            PairStatusKind.Full => PairingState.Full,
            PairStatusKind.Resting => PairingState.Resting,

            // A Withdrawn status can reach this side only when the other computer's newer request replaced the one
            // being followed. Lost and Gone mean the other computer let go of the request.
            _ => PairingState.Lost,
        };

        if (!state.IsLive())
            return End(entry, state);

        var expires = _now() + TimeSpan.FromSeconds(Math.Max(0, status.ExpiresIn));
        bool wrong = entry.State is PairingState.WaitingForCode && status.TriesLeft < entry.TriesLeft;

        bool changed = entry.State != state
                    || entry.TriesLeft != status.TriesLeft
                    || (wrong && !entry.WrongCode)
                    || Math.Abs((entry.ExpiresAtUtc - expires).TotalSeconds) >= 2;

        if (!changed)
            return false;

        entry.State = state;
        entry.TriesLeft = status.TriesLeft;
        entry.WrongCode |= wrong;
        entry.ExpiresAtUtc = expires;
        Touch(entry);
        return true;
    }

    /// <summary>Applies the facts that hold whenever they are asked. An asked pairing past its expiry has expired,
    /// whether or not the sweep has run. The caller holds the lock.</summary>
    private bool Settle(Entry entry) =>
        entry.Direction is PairingDirection.Asked && entry.State.IsLive() && _now() >= entry.ExpiresAtUtc
        && End(entry, PairingState.Expired);

    /// <summary>Ends a live pairing. The first ending wins, so an ended pairing is left alone. The caller holds the lock.</summary>
    private bool End(Entry entry, PairingState ending)
    {
        if (!entry.State.IsLive())
            return false;

        entry.State = ending;
        entry.WrongCode = false;
        entry.EndedAtUtc = _now();
        Touch(entry);
        return true;
    }

    private static void Touch(Entry entry)
    {
        entry.Rev++;
        entry.Signal();
    }

    private static string NewId() => SharingProtocol.ToBase64Url(RandomNumberGenerator.GetBytes(16));

    private sealed class Entry(PairingPeer peer, PairingDirection direction, long attempt, PairingState state)
    {
        public PairingPeer Peer { get; } = peer;
        public PairingDirection Direction { get; } = direction;
        public long Attempt { get; } = attempt;
        public PairingState State { get; set; } = state;
        public string Id { get; set; } = "";
        public string Code { get; set; } = "";

        /// <summary>Asked side: what makes each round and checks an opening.</summary>
        public PairingSecrets? Secrets { get; set; }

        /// <summary>Asked side: the current round. Its digits are <see cref="Code"/>, empty until the asking
        /// computer's nonce for that round arrives.</summary>
        public PairingRound? Round { get; set; }

        /// <summary>Asked side: the asking computer's commitment for <see cref="Round"/>, once it is taken. While it
        /// is null, the round's nonce is not revealed.</summary>
        public byte[]? AskerCommitment { get; set; }
        public int TriesLeft { get; set; }
        public bool WrongCode { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
        public long Rev { get; set; } = 1;
        public bool Dismissed { get; set; }
        public DateTime DismissedAtUtc { get; set; }
        public DateTime EndedAtUtc { get; set; }
        public DateTime CrossedSinceUtc { get; set; }

        /// <summary>Held status calls open right now. Asked side.</summary>
        public int Followers { get; set; }

        /// <summary>When the last held status call ended, or when the pairing was made.</summary>
        public DateTime LastFollowEndedUtc { get; set; }

        /// <summary>Completed and replaced on every change, which wakes every held status call.</summary>
        public TaskCompletionSource Changed { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Signal()
        {
            var old = Changed;
            Changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            old.TrySetResult();
        }

        public Pairing Freeze() => new()
        {
            Peer = Peer,
            Direction = Direction,
            Attempt = Attempt,
            Id = Id,
            State = State,
            Code = Code,
            TriesLeft = TriesLeft,
            WrongCode = WrongCode,
            ExpiresAtUtc = ExpiresAtUtc,
            Rev = Rev,
            Dismissed = Dismissed,
        };

        public PairStatus Status(DateTime now) => new()
        {
            State = State switch
            {
                PairingState.ShowingCode => PairStatusKind.ShowingCode,
                PairingState.Accepted => PairStatusKind.Accepted,
                PairingState.Declined => PairStatusKind.Declined,
                PairingState.Expired => PairStatusKind.Expired,
                PairingState.TooManyTries => PairStatusKind.TooManyTries,
                PairingState.Withdrawn => PairStatusKind.Withdrawn,
                PairingState.Full => PairStatusKind.Full,
                _ => PairStatusKind.Lost,
            },
            Rev = Rev,
            TriesLeft = TriesLeft,
            ExpiresIn = State.IsLive()
                ? (int)Math.Ceiling(Math.Max(0, (ExpiresAtUtc - now).TotalSeconds))
                : 0,

            // The round goes on the wire while the pairing is live. Its nonce is sent only after the asking computer
            // has committed for that round.
            Round = State.IsLive() && Round is { } live ? live.Number : 0,
            Commit = State.IsLive() && Round is { } committed ? SharingProtocol.ToBase64Url(committed.Commitment) : "",
            Shown = State.IsLive() && Round is { Code.Length: > 0 },
            Reveal = State.IsLive() && Round is { } revealed && AskerCommitment is not null
                ? SharingProtocol.ToBase64Url(revealed.Nonce)
                : "",
        };
    }
}
