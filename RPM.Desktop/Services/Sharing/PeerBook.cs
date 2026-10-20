using System.Text.Json;
using System.Text.Json.Serialization;
using RPM.Core.Sharing;

namespace RPM.Desktop.Services.Sharing;

/// <summary>
/// The computers this one shares with, written down.
///
/// <para>There are at most five. See <see cref="PeerTrust.Limit"/>. The rules about who may join are
/// kept there, not here. This class owns the file and the change notifications. Everything that shows
/// a peer reads the same list. A pairing made in a dialog must reach the deck, the settings page and
/// the server's own record of who may read the library.</para>
/// </summary>
public sealed partial class PeerBook
{
    private readonly object _gate = new();
    private IReadOnlyList<TrustedPeer> _peers = [];

    /// <summary>Whether the file has been read. It stays false when the file exists but could not be
    /// read, so the next look reads it again. Nothing is written until the file has been read. A failed
    /// read is never treated as an empty list. Otherwise the next pairing or unpairing would write that
    /// empty list over every paired computer. See RPM.Core.Files.StoreFile.</summary>
    private bool _loaded;

    private static string Path => System.IO.Path.Combine(LocalData.Dir, "peers.json");

    /// <summary>Raised (possibly off the UI thread) whenever the list changes.</summary>
    public event Action? Changed;

    /// <summary>Everyone this machine shares with, newest pairing last.</summary>
    public IReadOnlyList<TrustedPeer> Peers
    {
        get
        {
            lock (_gate)
            {
                if (!_loaded)
                {
                    var read = Read();
                    _peers = read.Value ?? [];
                    _loaded = read.MayWrite;
                }
                return _peers;
            }
        }
    }

    public bool IsFull => Peers.Count >= PeerTrust.Limit;

    /// <summary>Whether a peer is one this machine already shares with.</summary>
    public TrustedPeer? Find(string deviceId) => PeerTrust.Find(Peers, deviceId);

    /// <summary>
    /// Writes a peer in. <see cref="PeerTrustOutcome.Full"/> when there is no room, in which case
    /// nothing changed and the user has to decide what leaves.
    /// </summary>
    public PeerTrustOutcome Add(TrustedPeer peer)
    {
        PeerTrustOutcome outcome;
        lock (_gate)
        {
            var (result, next) = PeerTrust.Add(Peers, peer);
            outcome = result;
            if (outcome is PeerTrustOutcome.Full)
                return outcome;

            _peers = next;
            if (_loaded)
                Write(next);
        }

        Changed?.Invoke();
        return outcome;
    }

    /// <summary>
    /// Changes one record in place, under the book's lock, and returns it as it now stands. <paramref name="change"/>
    /// is given the current record and returns the new one, or null to leave it as it is. Null when the peer is not
    /// in the book.
    ///
    /// <para>Use this, not <see cref="Find"/> then <see cref="Add"/>, for a change made from another thread. Between
    /// a read and an <see cref="Add"/> another write can land, and the <see cref="Add"/> would put back the record
    /// as it was read.</para>
    /// </summary>
    public TrustedPeer? Update(string deviceId, Func<TrustedPeer, TrustedPeer?> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        TrustedPeer changed;
        lock (_gate)
        {
            if (PeerTrust.Find(Peers, deviceId) is not { } current)
                return null;
            if (change(current) is not { } next)
                return current;

            var (_, peers) = PeerTrust.Add(Peers, next);
            _peers = peers;
            if (_loaded)
                Write(peers);
            changed = PeerTrust.Find(peers, deviceId)!;
        }

        Changed?.Invoke();
        return changed;
    }

    /// <summary>Stops sharing with one computer, in both directions: it can no longer read this
    /// library, and its music leaves this one.</summary>
    public void Remove(string deviceId)
    {
        lock (_gate)
        {
            var next = PeerTrust.Remove(Peers, deviceId);
            if (next.Count == _peers.Count)
                return;

            _peers = next;
            if (_loaded)
                Write(next);
        }

        Changed?.Invoke();
    }

    /// <summary>Records that a peer was reachable just now. When the list is full, these times pick the
    /// peer that is used least. Nothing on screen changes, because a machine waking up is not a visible
    /// event.</summary>
    public void Seen(string deviceId)
    {
        lock (_gate)
        {
            if (PeerTrust.Find(Peers, deviceId) is not { } peer)
                return;

            var next = Peers
                .Select(p => ReferenceEquals(p, peer) ? p with { LastSeenUtcTicks = DateTime.UtcNow.Ticks } : p)
                .ToList();

            _peers = next;
            if (_loaded)
                Write(next);
        }
    }

    private static RPM.Core.Files.StoreRead<IReadOnlyList<TrustedPeer>> Read()
        => RPM.Core.Files.StoreFile.Load<IReadOnlyList<TrustedPeer>>(Path, text =>
        {
            var peers = JsonSerializer.Deserialize(text, PeerBookJson.Default.ListTrustedPeer);

            // A row with no key is not a peer. Trust rests on the key, so such a row could never
            // match anybody.
            return peers?.Where(p => p.PublicKey is { Length: > 0 }).Take(PeerTrust.Limit).ToList() ?? [];
        });

    private static void Write(IReadOnlyList<TrustedPeer> peers)
    {
        try
        {
            Directory.CreateDirectory(LocalData.Dir);
            File.WriteAllText(Path, JsonSerializer.Serialize(peers, PeerBookJson.Default.IReadOnlyListTrustedPeer));
        }
        catch
        {
            // If the data folder cannot be written, the pairing is lost at the next launch. The user
            // pairs again. That is better than failing now.
        }
    }
    /// <summary>The format of the trusted-peer file. Property names are camelCase and null values are
    /// left out. This matches the files that SharingProtocol.Json wrote, so those must still parse.
    /// A published build is Native AOT, which serialises from this and never by reflection.</summary>
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(List<TrustedPeer>))]
    [JsonSerializable(typeof(IReadOnlyList<TrustedPeer>))]
    private sealed partial class PeerBookJson : JsonSerializerContext;

}
