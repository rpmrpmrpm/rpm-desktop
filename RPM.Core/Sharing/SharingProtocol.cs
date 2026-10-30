using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RPM.Core.Sharing;

/// <summary>
/// What two RPMs say to each other, and where they say it.
///
/// <para>HTTP over TLS on the local network. Every route except <c>/hello</c> and the pairing routes needs
/// a token. A token is only issued to a key that this machine already trusts. The pairing routes after the
/// first one are reached by the pairing's id.</para>
/// </summary>
public static class SharingProtocol
{
    /// <summary>The version both ends must agree on. It is raised when the wire changes in a way that an older
    /// build would read wrongly. An older build is meant to see a peer it does not understand and leave it
    /// alone, not half-read it.</summary>
    /// <remarks>Version 4: nothing about the account is said before a peer has proved its key. The beacon and the
    /// greeting carry no account and no display name; the account is named inside the signed session proof
    /// (<see cref="SessionProof.Account"/>). A media URL carries a ticket for one route and id, not the token
    /// (<see cref="PeerSessions.Ticket"/>). A version 3 computer and a version 4 computer ignore each other,
    /// paired ones included, until both are updated.</remarks>
    public const int Version = 4;

    /// <summary>
    /// The pairing exchange both ends must speak. It is kept apart from <see cref="Version"/>, so that computers
    /// already paired keep sharing when one of them is updated.
    ///
    /// <para>Version 2 of the exchange works like this. For each round, the asked computer commits to its nonce.
    /// The asking computer then sends its own nonce, and the digits go on the asked computer's screen. The asking
    /// computer commits to the typed code, bound to both keys. The asked computer reveals its nonce. The asking
    /// computer checks the digits itself, and only then opens its commitment. See <see cref="PairingSecrets"/>
    /// and <see cref="PeerClient.PairingAsker"/>. A request without this is answered with 426. An offer without
    /// a commitment comes from an older computer, which the asking side will not pair with.</para>
    /// </summary>
    public const int Pairing = 2;

    /// <summary>What the beacon advertises. iOS must also declare the same type in <c>NSBonjourServices</c>. A
    /// service type that is not declared there is not refused. It simply never returns anything.</summary>
    public const string ServiceType = "_rpm._tcp";

    public const string HelloRoute = "/rpm/hello";
    public const string PairRoute = "/rpm/pair";

    /// <summary>A held status call on one pairing, by id. See <see cref="PairStatus"/>.</summary>
    public const string PairStatusRoute = "/rpm/pair/status";

    /// <summary>The asking computer's nonce for one round. It is sent once the asking computer has the asked
    /// computer's commitment for that round. Body: <see cref="PairNonce"/>. The digits then go on the asked
    /// computer's screen.</summary>
    public const string PairNonceRoute = "/rpm/pair/nonce";

    /// <summary>The asking computer's commitment to the typed code. Body: <see cref="PairCommit"/>. The answer
    /// reveals the asked computer's nonce for that round.</summary>
    public const string PairCommitRoute = "/rpm/pair/commit";

    /// <summary>The asking computer opens its commitment, or says its own check failed. Body:
    /// <see cref="PairOpen"/>.</summary>
    public const string PairOpenRoute = "/rpm/pair/open";

    /// <summary>The asking computer cancels. Body: <see cref="PairWithdraw"/>.</summary>
    public const string PairWithdrawRoute = "/rpm/pair/withdraw";
    public const string SessionRoute = "/rpm/session";
    public const string AccountRoute = "/rpm/account";
    public const string CatalogRoute = "/rpm/catalog";
    public const string TrackRoute = "/rpm/track/";
    public const string CoverRoute = "/rpm/cover/";

    /// <summary>The record itself: the album folder's disc.png. Any track of the album asks for it, the same way
    /// <see cref="CoverRoute"/> is asked for. It has its own route rather than a flag on the cover's. The two
    /// are separate pictures of one record. They are wanted at different moments and cached apart, and most
    /// records have one without the other.
    ///
    /// <para>This route is not part of the version number. A peer that does not know it answers 404. That is the
    /// same answer as a record with no disc art, and the borrower draws its own pressing either way.</para>
    /// </summary>
    public const string DiscRoute = "/rpm/disc/";

    public const string ArtRoute = "/rpm/art/";

    /// <summary>
    /// Whether a URL is a track being served by another computer on this network, as against a store's
    /// presigned link.
    ///
    /// <para>The player asks this when a stream stops in the middle of a record. The two kinds of failure
    /// go wrong in different ways, and each is recovered from in its own way. A computer that has left the
    /// network is written off, and the record is looked for in the cloud. A store link that broke is simply
    /// signed again.</para>
    /// </summary>
    public static bool IsPeerTrackUrl(string? url) =>
        url is not null && url.Contains(TrackRoute, StringComparison.Ordinal);

    /// <summary>The TXT keys. They are kept short, because a TXT record is small. Every byte of it is broadcast
    /// each time anybody asks.</summary>
    public static class Txt
    {
        public const string Version = "v";
        public const string DeviceId = "id";
        public const string Name = "n";

        public const string PublicKey = "k";
    }

    /// <summary>One set of options for both ends. camelCase, because this is a wire format that a phone, a
    /// desktop and any other client all read.</summary>
    /// <remarks>The resolver lets this work in a published build. Native AOT switches off reflection-based
    /// serialization. So options without a resolver can describe no type at all. A caller that serializes a
    /// type that this context does not list must bring its own resolver.</remarks>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = SharingJson.Default,
    };

    /// <summary>Base64url without padding. It is used for keys and signatures that travel in a TXT record, a
    /// URL or a small JSON body.</summary>
    public static string ToBase64Url(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>The other direction. Null for anything that is not base64url. A malformed key from a beacon
    /// means a peer to ignore. It is not an exception.</summary>
    public static byte[]? FromBase64Url(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var padded = text.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", 1 => "===", _ => "" };

        try { return Convert.FromBase64String(padded); }
        catch (FormatException) { return null; }
    }
}

/// <summary>What a machine says about itself before anybody has proved anything. The pairing offer reads it,
/// because it needs a name to show a person.</summary>
public sealed record PeerHello
{
    [JsonPropertyName("version")] public int Version { get; init; } = SharingProtocol.Version;
    [JsonPropertyName("deviceId")] public required string DeviceId { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("publicKey")] public required string PublicKey { get; init; }

    /// <summary>How many tracks are on offer. It is shown in the offer, so that "share libraries?" is a question
    /// with a size in it.</summary>
    [JsonPropertyName("trackCount")] public int TrackCount { get; init; }
}

/// <summary>
/// One machine asking another to share.
///
/// <para>It is sent by the side whose user pressed the button.</para>
/// </summary>
public sealed record PairRequest
{
    [JsonPropertyName("version")] public int Version { get; init; } = SharingProtocol.Version;
    [JsonPropertyName("publicKey")] public required string PublicKey { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }

    /// <summary>The display name of whoever is at the asking machine, or empty. This is what the question on the
    /// answering screen is headed with. Without it, the question shows only a machine name.</summary>
    [JsonPropertyName("face")] public string Face { get; init; } = "";


    /// <summary>
    /// A random value that only computers before pairing version 2 read. Those computers make the digits from
    /// it. From pairing version 2 on, the asking computer's nonce is sent per round, after the asked computer's
    /// commitment (<see cref="PairNonce"/>). This field is then ignored.
    ///
    /// <para>It is still sent, so that an older computer answers with an offer rather than refusing the
    /// request. An offer without a commitment tells the asking side that the other computer needs updating. A
    /// refusal would read as unreachable.</para>
    /// </summary>
    [JsonPropertyName("nonce")] public required string Nonce { get; init; }

    /// <summary>The pairing exchange the asking computer speaks (<see cref="SharingProtocol.Pairing"/>). There is
    /// no default. A request from a build that predates the field reads as 0 and is refused. A default here would
    /// hide that.</summary>
    [JsonPropertyName("pairing")] public int Pairing { get; init; }
}

/// <summary>Where a pairing stands, as the asked computer reports it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PairStatusKind>))]
public enum PairStatusKind
{
    /// <summary>The code is on the asked computer's screen.</summary>
    ShowingCode,

    /// <summary>The right code was typed. Both computers record each other.</summary>
    Accepted,

    /// <summary>The person at the asked computer pressed Decline.</summary>
    Declined,

    /// <summary>The request is older than its lifetime.</summary>
    Expired,

    /// <summary>Three wrong codes.</summary>
    TooManyTries,

    /// <summary>The asking computer cancelled, or replaced this request with a newer one.</summary>
    Withdrawn,

    /// <summary>The asked computer stopped seeing the asking one follow the request, or stopped sharing.</summary>
    Lost,

    /// <summary>The asked computer already shares with five.</summary>
    Full,

    /// <summary>The asked computer has no pairing with that id. It was restarted, or the pairing was removed some
    /// time ago.</summary>
    Gone,

    /// <summary>The asked computer takes no requests for a while. This happens after too many wrong codes across
    /// requests.</summary>
    Resting,
}

/// <summary>
/// One pairing, as the asked computer reports it. The asked computer is the source of truth for everything after
/// the offer. The asking computer copies this onto its own board.
/// </summary>
public sealed record PairStatus
{
    [JsonPropertyName("state")] public required PairStatusKind State { get; init; }

    /// <summary>Goes up on every change. A held status call sends the last one it saw, and is answered when it
    /// differs.</summary>
    [JsonPropertyName("rev")] public long Rev { get; init; }

    [JsonPropertyName("triesLeft")] public int TriesLeft { get; init; }

    /// <summary>Seconds until the request expires. Zero once it has ended.</summary>
    [JsonPropertyName("expiresIn")] public int ExpiresIn { get; init; }

    /// <summary>The current round, counting from 1. A wrong code starts a new round, with new digits on the asked
    /// computer's screen.</summary>
    [JsonPropertyName("round")] public int Round { get; init; }

    /// <summary>The asked computer's commitment to this round's nonce, base64url. It is empty from a computer that
    /// does not speak <see cref="SharingProtocol.Pairing"/> version 2.</summary>
    [JsonPropertyName("commit")] public string Commit { get; init; } = "";

    /// <summary>Whether the asked computer has the asking computer's nonce for this round. When it has, the digits
    /// go on its screen.</summary>
    [JsonPropertyName("shown")] public bool Shown { get; init; }

    /// <summary>This round's nonce, base64url. It is sent once the asking computer has committed to a code for the
    /// round. It is empty before that.</summary>
    [JsonPropertyName("reveal")] public string Reveal { get; init; } = "";

    /// <summary>The answer given for an unknown id.</summary>
    public static PairStatus Gone { get; } = new() { State = PairStatusKind.Gone };
}

/// <summary>
/// The asked machine's answer, sent at once: who it is, the pairing id, and where the pairing stands.
///
/// <para>It carries a commitment to the asked machine's nonce, not the nonce itself. The code is computed from both
/// keys and both nonces. The asking machine already has three of those four values. With the fourth, it could
/// compute the code itself without anybody reading it off the other screen. That reading is the only thing the code
/// proves. The nonce is revealed only once the asking machine has committed to what was typed
/// (<see cref="PairCommit"/>). From then on it decides nothing. A wrong code starts a new round with a new nonce.</para>
///
/// <para>The id is the only way to reach the pairing afterwards. Status, nonce, commit, open and withdraw all use it.
/// It is sent once, here, over the connection that the asking side pinned to the beacon's key. So nobody else on the
/// network can read, answer or cancel a pairing that they did not start.</para>
/// </summary>
public sealed record PairOffer
{
    [JsonPropertyName("deviceId")] public required string DeviceId { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }

    /// <summary>The display name on the account at that machine, or empty.</summary>
    [JsonPropertyName("face")] public string Face { get; init; } = "";

    [JsonPropertyName("publicKey")] public required string PublicKey { get; init; }

    /// <summary>The pairing id. It is empty when no pairing was made. <see cref="Status"/> says why.</summary>
    [JsonPropertyName("id")] public string Id { get; init; } = "";

    [JsonPropertyName("status")] public required PairStatus Status { get; init; }
}

/// <summary>The asking computer's nonce for one round. See <see cref="PeerClient.PairingAsker"/> for why it is
/// sent only after the asked computer's commitment for the round.</summary>
public sealed record PairNonce
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("round")] public required int Round { get; init; }

    /// <summary>Base64url.</summary>
    [JsonPropertyName("nonce")] public required string Nonce { get; init; }
}

/// <summary>The asking computer's commitment to the code typed there, for one round. The code itself never crosses
/// the network.</summary>
public sealed record PairCommit
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("round")] public required int Round { get; init; }

    /// <summary><see cref="PairingCode.CommitCode"/>, base64url.</summary>
    [JsonPropertyName("commit")] public required string Commit { get; init; }
}

/// <summary>The asking computer opens its commitment for one round. Or it says that its own check of the revealed
/// nonce failed (an empty <see cref="Opening"/>). That counts as a wrong code.</summary>
public sealed record PairOpen
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("round")] public required int Round { get; init; }
    [JsonPropertyName("opening")] public required string Opening { get; init; }
}

/// <summary>The asking computer cancels one pairing.</summary>
public sealed record PairWithdraw
{
    [JsonPropertyName("id")] public required string Id { get; init; }
}

/// <summary>
/// Which account the machine at the other end is signed into. It is told only to a computer that this one already
/// shares with.
///
/// <para>It is not in the beacon, where the account is only a fingerprint. A broadcast that anybody on a café network
/// can read is no place for an account identifier. This is asked for over a connection that has already proved which
/// key it holds. It buys one thing: the grant that keeps a friend's cloud-stored records playing once everybody has
/// left this network. That is a statement that one account makes about another. So it needs the other account's
/// name.</para>
///
/// <para>It is empty when that machine is signed out. That is an answer. There is no account to grant anything to, and
/// the sharing stays where it was arranged.</para>
/// </summary>
public sealed record PeerAccount
{
    [JsonPropertyName("userId")] public string UserId { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
}

/// <summary>Step one of getting a token: ask for something to sign.</summary>
public sealed record SessionChallenge
{
    [JsonPropertyName("challenge")] public required string Challenge { get; init; }

    /// <summary>Seconds that the challenge stands. The time is short, because it must be answered at once or not
    /// at all.</summary>
    [JsonPropertyName("expiresIn")] public int ExpiresIn { get; init; }
}

/// <summary>Step two: here is the key, and here is the challenge signed by it.</summary>
public sealed record SessionProof
{
    [JsonPropertyName("publicKey")] public required string PublicKey { get; init; }
    [JsonPropertyName("challenge")] public required string Challenge { get; init; }
    [JsonPropertyName("signature")] public required string Signature { get; init; }

    /// <summary>The account the peer is signed into (the user id), or empty. Covered by the signature (see
    /// <see cref="PeerSessions.SignedBytes"/>), and sent only to a machine whose key the peer has pinned.</summary>
    [JsonPropertyName("account")] public string Account { get; init; } = "";
}

/// <summary>The token, and how long it is good for.</summary>
public sealed record SessionGrant
{
    [JsonPropertyName("token")] public required string Token { get; init; }
    [JsonPropertyName("expiresIn")] public int ExpiresIn { get; init; }

    /// <summary>Whether the peer is served as one of the user's own machines or as a guest. The borrower needs to
    /// know this. One kind lands in the user's own collection. The other does not.</summary>
    [JsonPropertyName("kind")] public PeerKind Kind { get; init; }
}

/// <summary>
/// One track, as a peer describes it. It has what the grid, the wall and the queue need. It has nothing that
/// belongs to the person rather than to the collection: no play counts, no favourites, and no dates when somebody
/// listened to something.
/// </summary>
public sealed record PeerTrack
{
    [JsonPropertyName("rpmId")] public required string RpmId { get; init; }

    /// <summary>Where it sits in the owner's collection, relative to their root. This is what makes a folder an
    /// album here as well as there.</summary>
    [JsonPropertyName("path")] public required string RelativePath { get; init; }

    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("artist")] public string? Artist { get; init; }
    [JsonPropertyName("albumArtist")] public string? AlbumArtist { get; init; }
    [JsonPropertyName("album")] public string? Album { get; init; }
    [JsonPropertyName("genre")] public string? Genre { get; init; }
    [JsonPropertyName("composer")] public string? Composer { get; init; }
    [JsonPropertyName("work")] public string? Work { get; init; }
    [JsonPropertyName("movement")] public string? MovementName { get; init; }
    [JsonPropertyName("movementNo")] public int? MovementNumber { get; init; }
    [JsonPropertyName("discSubtitle")] public string? DiscSubtitle { get; init; }

    [JsonPropertyName("track")] public uint TrackNumber { get; init; }
    [JsonPropertyName("disc")] public uint DiscNumber { get; init; }
    [JsonPropertyName("discs")] public uint DiscCount { get; init; }
    [JsonPropertyName("year")] public uint Year { get; init; }

    [JsonPropertyName("seconds")] public double DurationSeconds { get; init; }
    [JsonPropertyName("size")] public long SizeBytes { get; init; }
    [JsonPropertyName("added")] public long AddedUtcTicks { get; init; }

    [JsonPropertyName("channels")] public int? Channels { get; init; }
    [JsonPropertyName("sampleRate")] public int? SampleRate { get; init; }
    [JsonPropertyName("bits")] public int? BitsPerSample { get; init; }
    [JsonPropertyName("bitrate")] public int? BitrateKbps { get; init; }
    [JsonPropertyName("codec")] public string? CodecDescription { get; init; }

    [JsonPropertyName("lufs")] public double? LoudnessLufs { get; init; }
    [JsonPropertyName("peak")] public double? TruePeak { get; init; }

    /// <summary>The canonical hash, when the owner knows it. It tells this machine that a peer's track and one of its
    /// own are the same audio.</summary>
    [JsonPropertyName("hash")] public string? ContentHash { get; init; }

    /// <summary>
    /// Whether the file's own flag marks the lyrics as explicit. The value is the same number as
    /// <c>ContentAdvisory</c>'s. Zero means no flag, which is the case for most files.
    ///
    /// <para>It is not a version bump. An older build sends nothing, which reads as zero, so its borrowed rows
    /// have no mark. The same field, with the same numbers, is the cloud listing's
    /// <c>CloudTrackTags.Advisory</c>. The mirror reads it through there.</para>
    /// </summary>
    [JsonPropertyName("advisory")] public int Advisory { get; init; }

    /// <summary>Whether the owner still holds the file. A track the owner has offloaded is served by redirecting to
    /// the cloud, not from their disk.</summary>
    [JsonPropertyName("cloudOnly")] public bool CloudOnly { get; init; }

    /// <summary>
    /// Whether the owner's cloud holds a copy of this track, whether or not their disk does too.
    ///
    /// <para>This decides which borrowed records survive the owner's computer going away. It is a wider set than
    /// <see cref="CloudOnly"/>. A track that is uploaded and still on the owner's disk is in the store just as much
    /// as one the owner has since offloaded. Read on its own, the offloaded flag says only "not on
    /// their disk". It does not say what the store has, so a borrower must not treat it as the whole set.</para>
    ///
    /// <para>An older peer's listing has no such field, and it reads as false. The union with
    /// <see cref="CloudOnly"/> keeps that build's offloaded records playable.</para>
    /// </summary>
    [JsonPropertyName("inCloud")] public bool InCloud { get; init; }
}

/// <summary>
/// One album's marks as another of the listener's own computers holds them, with the moment somebody last said any
/// of it.
///
/// <para>Sent only to a machine signed into the same account. A guest is given a shelf. A shelf is what is on it, not
/// what its owner thinks of any of it. See SharingService, which decides.</para>
/// </summary>
public sealed record PeerAlbum
{
    /// <summary>The album's folder, relative to the shelf's root. Both machines already file records under this
    /// key.</summary>
    [JsonPropertyName("folder")] public required string Folder { get; init; }

    [JsonPropertyName("favorite")] public bool Favorite { get; init; }
    [JsonPropertyName("rating")] public int Rating { get; init; }
    [JsonPropertyName("review")] public string? Review { get; init; }
    [JsonPropertyName("reviewAt")] public long ReviewUpdatedUtcTicks { get; init; }

    [JsonPropertyName("releaseProvider")] public string? ReleaseProvider { get; init; }
    [JsonPropertyName("releaseId")] public string? ReleaseId { get; init; }
    [JsonPropertyName("rpmAlbumId")] public string? RpmAlbumId { get; init; }
    [JsonPropertyName("groupId")] public string? ReleaseGroupId { get; init; }
    [JsonPropertyName("discId")] public string? DiscId { get; init; }
    [JsonPropertyName("barcode")] public string? Barcode { get; init; }
    [JsonPropertyName("catno")] public string? Catno { get; init; }
    [JsonPropertyName("released")] public string? Released { get; init; }

    /// <summary>What the pressing is, in the catalogue's words. It travels with the rest of the record's identity.
    /// A borrowed copy is the lender's pressing. A guest looking at somebody else's shelf cannot work out which
    /// pressing it is from the files.</summary>
    [JsonPropertyName("edition")] public string? Edition { get; init; }

    /// <summary>The narrow labels the catalogue files the release under, separated by semicolons. They travel for the
    /// same reason as the popular track. This is the same public answer that this machine would fetch for itself.
    /// Taking the lender's saves a request against somebody's rate limit.</summary>
    [JsonPropertyName("styles")] public string? Styles { get; init; }

    /// <summary>The series the record is one volume of, and which volume. "" for none. Absent from an older
    /// peer's listing, which reads as null and leaves the receiver's own answer standing.</summary>
    [JsonPropertyName("series")] public string? Series { get; init; }
    [JsonPropertyName("seriesVolume")] public string? SeriesVolume { get; init; }

    /// <summary>The record's best-known song, as the lender's machine was told it. It is not the lender's opinion,
    /// and it is not their listening. It is the same public answer that this machine would fetch for itself. Taking
    /// theirs saves a guest three calls to two rate-limited services.</summary>
    [JsonPropertyName("popularTrack")] public string? PopularTrack { get; init; }

    /// <summary>When the lender last asked. It travels with the answer, so a guest inherits its age too. The guest
    /// asks again a week after the answer was really fetched, not a week after it was handed over.</summary>
    [JsonPropertyName("popularAskedAt")] public long PopularAskedUtcTicks { get; init; }

    [JsonPropertyName("classical")] public bool? Classical { get; init; }
    [JsonPropertyName("normalize")] public bool? Normalize { get; init; }

    /// <summary>When somebody last changed any of the above, on whichever machine did it. For the merge rule as a
    /// whole, see <c>LibraryDatabase.ApplyAlbumMetaEdits</c>.</summary>
    [JsonPropertyName("editedAt")] public long EditedUtcTicks { get; init; }
}

/// <summary>
/// One folder or playlist out of somebody's tree, with what it holds.
///
/// <para>It is flat, not nested, because the tree is small. Both ends already store a flat list with parent links.
/// <see cref="Id"/> and <see cref="ParentId"/> are the owner's row ids. They mean nothing on this side, except that
/// they join these rows back into a tree. The mirror files them under ids of its own.</para>
///
/// <para>Nothing here says whether the playlist is in its owner's cloud. That looks like the fact the borrower needs:
/// a playlist that plays after its owner has gone home. But it is one step short of that fact. What decides it is
/// whether every song in the playlist is in the cloud. The track listing beside this already says that. See
/// <c>LibraryDatabase.CloudRowsOnly</c>, which is where the shelf narrows.</para>
/// </summary>
public sealed record PeerPlaylist
{
    [JsonPropertyName("id")] public required long Id { get; init; }
    [JsonPropertyName("parent")] public long? ParentId { get; init; }

    /// <summary>A folder holds other rows. A playlist holds <see cref="Items"/>.</summary>
    [JsonPropertyName("folder")] public bool IsFolder { get; init; }

    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("sort")] public int Sort { get; init; }

    [JsonPropertyName("items")] public IReadOnlyList<PeerPlaylistItem> Items { get; init; } = [];

    /// <summary>
    /// Whether the owner chose this row's picture, as a stamp of the picture. The stamp is a hash of the bytes that
    /// their computer serves for it under <see cref="SharingProtocol.ArtRoute"/> as <c>pl:</c><see cref="Id"/>.
    /// Null means the automatic cover. The borrower draws that for itself from the same records.
    ///
    /// <para>A stamp is used rather than a flag. The borrower can keep its copy under the stamp, and fetch again only
    /// when the picture changes. The stamp is part of the listing's version for the same reason.</para>
    ///
    /// <para>It is not a version bump. An older build sends nothing, which means the automatic cover.</para>
    /// </summary>
    [JsonPropertyName("picture")] public string? Picture { get; init; }
}

/// <summary>
/// One of the owner's generated lists, as their computer cut it.
///
/// <para><b>Their cut, not one the borrower makes.</b> A generated list is percentiles over a whole collection. The
/// borrower does not have the owner's collection. It has only a listing of it. Cutting again here would produce lists
/// with the owner's names on them, which the owner has never seen. That is worse than not showing them at all.</para>
///
/// <para><b>It is the same cut the phone gets. What differs is what can be played.</b> On the same network, the
/// borrower can ask for every track in it, because the owner's machine serves them. Away from the network, only the
/// records the owner put in the cloud resolve. So the list is thinner. This is the phone's answer, reached from the
/// other side.</para>
/// </summary>
public sealed record PeerMoodList
{
    /// <summary>The mood, as the owner's <c>SmartPlaylistKind</c> spells it.</summary>
    [JsonPropertyName("kind")] public required string Kind { get; init; }

    /// <summary>The shelf it is narrowed to, or null for the whole list.</summary>
    [JsonPropertyName("genre")] public string? Genre { get; init; }

    /// <summary>The first year of the decade it is narrowed to, or zero for all of them.</summary>
    [JsonPropertyName("decade")] public int Decade { get; init; }

    /// <summary>
    /// Where the list sits in the owner's own tree, outermost first. For example "Moods", or "Moods" then the mood,
    /// or "Decades". It is empty from an owner whose build predates this field.
    ///
    /// <para><b>It is sent, not worked out by the borrower.</b> If the borrower worked it out for itself, it would
    /// make one flat folder holding the eras and the mood flavours together. Nobody chose that arrangement. How a
    /// shelf is arranged is part of the shelf.</para>
    /// </summary>
    [JsonPropertyName("folder")] public IReadOnlyList<string> Folder { get; init; } = [];

    /// <summary>RPM ids, best first. These are the same ids that the tracks in this catalogue carry. The borrower's
    /// mirror uses them to find each row, and a track is later asked for by the same id.</summary>
    [JsonPropertyName("rpmIds")] public IReadOnlyList<string> RpmIds { get; init; } = [];
}

/// <summary>One entry in a playlist: a song, or a reference to another playlist in the same tree.</summary>
public sealed record PeerPlaylistItem
{
    /// <summary>The song's RPM id. The borrower's mirror uses it to find the track row, and the track is later asked
    /// for by it. Null for an entry that its owner made before the file had an id.</summary>
    [JsonPropertyName("rpmId")] public string? RpmId { get; init; }

    /// <summary>The same song's path in the owner's collection. It is the fallback when there is no id. It is the
    /// same key that the track listing files it under.</summary>
    [JsonPropertyName("path")] public string? RelPath { get; init; }

    /// <summary>The owner's id of a nested playlist, when this entry is one.</summary>
    [JsonPropertyName("ref")] public long? Ref { get; init; }
}

/// <summary>
/// A peer's whole shared collection, and the version of it.
///
/// <para>It is sent whole, not as a change list. The version is a hash of what was sent. On the second visit, the
/// borrower asks "still <c>abc123</c>?" and is told yes in a few hundred bytes. A library that changed crosses the
/// local network again. On a local network, that costs less than keeping an accurate change log would.</para>
/// </summary>
public sealed record PeerCatalog
{
    [JsonPropertyName("version")] public required string Version { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("tracks")] public required IReadOnlyList<PeerTrack> Tracks { get; init; }

    /// <summary>What the owner has said about the albums, for one of their own machines. It is empty for a guest.
    /// It is also empty in anything an older build sends. That reads as "this machine has nothing to merge", which is
    /// exactly right.</summary>
    [JsonPropertyName("albums")] public IReadOnlyList<PeerAlbum> Albums { get; init; } = [];

    /// <summary>
    /// The owner's folders and playlists, for a guest looking at their shelf. It is empty for another of the owner's
    /// own machines. Those playlists travel through the cloud, and sending them here would give this library a second
    /// copy of a tree it already has. It is also empty in anything an older build sends. That reads as a shelf with no
    /// playlists on it.
    /// </summary>
    [JsonPropertyName("playlists")] public IReadOnlyList<PeerPlaylist> Playlists { get; init; } = [];

    /// <summary>
    /// The owner's generated lists, as their computer last cut them. It is empty in anything an older build sends.
    /// That reads as a shelf with no generated lists on it.
    ///
    /// <para>They travel with the catalogue, not on their own. That is what makes "when a new cut is made" work
    /// without a second channel. The cut is part of the version hash below. So a new cut is a new catalogue, and the
    /// borrower collects it on its next visit, the same way it collects a renamed album.</para>
    /// </summary>
    [JsonPropertyName("moodLists")] public IReadOnlyList<PeerMoodList> MoodLists { get; init; } = [];

    /// <summary>
    /// The version of a listing: a hash over what identifies each track and everything about it that would show. It
    /// also covers the albums' marks beside them, and the playlists built from them. Two catalogues with the same
    /// version are the same catalogue. The borrower can be told so, instead of being sent it again.
    ///
    /// <para>The albums are in the hash, and that matters. An album marked as classical changes no track row at all.
    /// A version taken over the tracks alone would answer "still abc123" to a machine that has something to collect.
    /// The change would then wait for the next track to be added, possibly forever. A playlist that is renamed,
    /// reordered or added to is the same case. So it is in the hash for the same reason.</para>
    /// </summary>
    public static string VersionOf(
        IReadOnlyList<PeerTrack> tracks, IReadOnlyList<PeerAlbum> albums,
        IReadOnlyList<PeerPlaylist> playlists, IReadOnlyList<PeerMoodList> moodLists)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(albums);
        ArgumentNullException.ThrowIfNull(playlists);
        ArgumentNullException.ThrowIfNull(moodLists);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var track in tracks.OrderBy(t => t.RpmId, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(track, SharingJson.Default.PeerTrack)));
            hash.AppendData("\n"u8);
        }

        foreach (var album in albums.OrderBy(a => a.Folder, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(album, SharingJson.Default.PeerAlbum)));
            hash.AppendData("\n"u8);
        }

        foreach (var playlist in playlists.OrderBy(p => p.Id))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(playlist, SharingJson.Default.PeerPlaylist)));
            hash.AppendData("\n"u8);
        }

        // The mood lists are in the hash for the same reason as the albums. A new cut changes no track row at all.
        // A version taken without them would answer "still abc123" to a borrower with a whole shelf of new lists to
        // collect. The change would then wait for the next track to be added, possibly forever. Including the lists
        // here is what makes a new cut reach the borrower.
        foreach (var list in moodLists.OrderBy(l => l.Kind, StringComparer.Ordinal)
                                      .ThenBy(l => l.Genre, StringComparer.Ordinal)
                                      .ThenBy(l => l.Decade))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(list, SharingJson.Default.PeerMoodList)));
            hash.AppendData("\n"u8);
        }

        return SharingProtocol.ToBase64Url(hash.GetHashAndReset()[..12]);
    }
}

/// <summary>
/// Every shape the sharing protocol puts on the wire, as the System.Text.Json source generator describes them.
/// </summary>
/// <remarks>
/// <para>The options are the same ones that <see cref="SharingProtocol.Json"/> carries. They are repeated here, not
/// referenced, because the generator reads attributes, not fields. The two must agree. camelCase and omitted nulls
/// are the wire format that a phone on the other end parses.</para>
///
/// <para>A published build is Native AOT. Nothing serializes there without metadata written at build time. A type
/// that is added to the protocol and not added here will compile and ship. Then it throws at the first request that
/// carries it.</para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PeerHello))]
[JsonSerializable(typeof(PairRequest))]
[JsonSerializable(typeof(PairOffer))]
[JsonSerializable(typeof(PairStatus))]
[JsonSerializable(typeof(PairNonce))]
[JsonSerializable(typeof(PairCommit))]
[JsonSerializable(typeof(PairOpen))]
[JsonSerializable(typeof(PairWithdraw))]
[JsonSerializable(typeof(PeerAccount))]
[JsonSerializable(typeof(SessionChallenge))]
[JsonSerializable(typeof(SessionProof))]
[JsonSerializable(typeof(SessionGrant))]
[JsonSerializable(typeof(PeerTrack))]
[JsonSerializable(typeof(PeerAlbum))]
[JsonSerializable(typeof(PeerPlaylist))]
[JsonSerializable(typeof(PeerMoodList))]
[JsonSerializable(typeof(PeerPlaylistItem))]
[JsonSerializable(typeof(PeerCatalog))]
public sealed partial class SharingJson : JsonSerializerContext;
