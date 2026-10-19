using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using RPM.Core.Sharing;

namespace RPM.Core.Tests;

/// <summary>
/// The asking computer's checks, against an asked computer played by the test. The real server is
/// honest, so what a machine in the middle could send is sent from here.
/// </summary>
public class PairingAskerTests
{
    private readonly PeerIdentity _me = PeerIdentity.Create();
    private readonly byte[] _ownerKey = PeerIdentity.Create().PublicKey;

    /// <summary>
    /// The asked computer, or whatever answers in its place. Commits to <see cref="Nonce"/>, reveals
    /// <see cref="Reveal"/> (the committed nonce unless a test sets another), and keeps what it was
    /// sent.
    /// </summary>
    private sealed class Owner : HttpMessageHandler
    {
        public byte[] Nonce { get; set; } = PairingCode.Nonce();
        public byte[]? Reveal { get; set; }
        public List<byte[]> AskerNonces { get; } = [];
        public byte[]? Commitment { get; private set; }
        public byte[]? Opening { get; private set; }
        public int Calls { get; private set; }

        public PairStatus Status(bool revealed = false) => new()
        {
            State = PairStatusKind.ShowingCode,
            Rev = Calls + 1,
            TriesLeft = 3,
            Round = 1,
            Commit = SharingProtocol.ToBase64Url(PairingCode.CommitNonce(Nonce)),
            Shown = AskerNonces.Count > 0,
            Reveal = revealed ? SharingProtocol.ToBase64Url(Reveal ?? Nonce) : "",
        };

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var path = request.RequestUri!.AbsolutePath;

            if (path == SharingProtocol.PairNonceRoute)
            {
                var sent = await request.Content!.ReadFromJsonAsync(SharingJson.Default.PairNonce, cancellationToken);
                AskerNonces.Add(SharingProtocol.FromBase64Url(sent!.Nonce)!);
                return Answer(Status());
            }

            if (path == SharingProtocol.PairCommitRoute)
            {
                var sent = await request.Content!.ReadFromJsonAsync(SharingJson.Default.PairCommit, cancellationToken);
                Commitment = SharingProtocol.FromBase64Url(sent!.Commit);
                return Answer(Status(revealed: true));
            }

            if (path == SharingProtocol.PairOpenRoute)
            {
                var sent = await request.Content!.ReadFromJsonAsync(SharingJson.Default.PairOpen, cancellationToken);
                Opening = SharingProtocol.FromBase64Url(sent!.Opening) ?? [];
                return Answer(Status());
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Answer(PairStatus status) =>
            new(HttpStatusCode.OK) { Content = JsonContent.Create(status, SharingJson.Default.PairStatus) };
    }

    private PeerClient Client(Owner owner) => new(_me, "192.0.2.1", 51733, _ownerKey, owner);

    /// <summary>The digits the asking computer computes from a revealed nonce.</summary>
    private string Digits(Owner owner, byte[] ownerNonce) =>
        PairingCode.For(_me.PublicKey, _ownerKey, owner.AskerNonces[0], ownerNonce);

    [Fact]
    public async Task The_right_code_is_opened_and_the_opening_shows_the_code_bound_to_both_keys()
    {
        var owner = new Owner();
        using var client = Client(owner);
        var asker = new PeerClient.PairingAsker(client);

        await asker.JoinAsync("id", owner.Status(), CancellationToken.None);
        var code = Digits(owner, owner.Nonce);
        await asker.TypeAsync("id", code, CancellationToken.None);

        Assert.NotEmpty(owner.Opening!);
        Assert.Equal(owner.Commitment, PairingCode.CommitCode(_me.PublicKey, _ownerKey, owner.Opening!, code));
    }

    [Fact]
    public async Task A_wrong_code_is_not_opened()
    {
        var owner = new Owner();
        using var client = Client(owner);
        var asker = new PeerClient.PairingAsker(client);

        await asker.JoinAsync("id", owner.Status(), CancellationToken.None);
        var right = Digits(owner, owner.Nonce);
        var wrong = ((int.Parse(right, System.Globalization.CultureInfo.InvariantCulture) + 1) % 1_000_000)
            .ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        await asker.TypeAsync("id", wrong, CancellationToken.None);

        Assert.NotNull(owner.Commitment);
        Assert.Empty(owner.Opening!);
    }

    /// <summary>
    /// A machine in the middle reveals a nonce it chose after seeing what this side sent, one that
    /// makes the digits here match the ones the user typed. It is not the nonce it committed to, so
    /// nothing is opened.
    /// </summary>
    [Fact]
    public async Task A_revealed_nonce_that_is_not_the_committed_one_is_not_believed()
    {
        var owner = new Owner();
        using var client = Client(owner);
        var asker = new PeerClient.PairingAsker(client);

        await asker.JoinAsync("id", owner.Status(), CancellationToken.None);
        owner.Reveal = PairingCode.Nonce();
        await asker.TypeAsync("id", Digits(owner, owner.Reveal), CancellationToken.None);

        Assert.Empty(owner.Opening!);
    }

    /// <summary>
    /// The same, with the machine in the middle also sending a new commitment for the round after it
    /// has this side's nonce. The commitment checked is the one received before the nonce was sent.
    /// </summary>
    [Fact]
    public async Task A_commitment_changed_after_this_sides_nonce_went_out_is_not_believed()
    {
        var owner = new Owner();
        using var client = Client(owner);
        var asker = new PeerClient.PairingAsker(client);

        await asker.JoinAsync("id", owner.Status(), CancellationToken.None);

        owner.Nonce = PairingCode.Nonce();
        await asker.JoinAsync("id", owner.Status() with { Shown = false }, CancellationToken.None);
        await asker.TypeAsync("id", Digits(owner, owner.Nonce), CancellationToken.None);

        Assert.Empty(owner.Opening!);
    }

    [Fact]
    public async Task No_nonce_goes_out_before_the_other_computer_has_committed()
    {
        var owner = new Owner();
        using var client = Client(owner);
        var asker = new PeerClient.PairingAsker(client);

        var uncommitted = owner.Status() with { Commit = "" };
        Assert.Null(await asker.JoinAsync("id", uncommitted, CancellationToken.None));
        Assert.Null(await asker.TypeAsync("id", "123456", CancellationToken.None));

        Assert.Equal(0, owner.Calls);
    }

    [Fact]
    public async Task The_nonce_for_a_round_is_the_same_however_often_it_is_sent_and_a_new_round_gets_a_new_one()
    {
        var owner = new Owner();
        using var client = Client(owner);
        var asker = new PeerClient.PairingAsker(client);

        var first = owner.Status();
        await asker.JoinAsync("id", first, CancellationToken.None);
        await asker.JoinAsync("id", first, CancellationToken.None);

        owner.Nonce = PairingCode.Nonce();
        await asker.JoinAsync("id", owner.Status() with { Round = 2, Shown = false }, CancellationToken.None);

        Assert.Equal(3, owner.AskerNonces.Count);
        Assert.Equal(owner.AskerNonces[0], owner.AskerNonces[1]);
        Assert.NotEqual(owner.AskerNonces[0], owner.AskerNonces[2]);
    }

    [Fact]
    public async Task A_round_already_showing_its_digits_is_not_sent_a_nonce()
    {
        var owner = new Owner();
        using var client = Client(owner);
        var asker = new PeerClient.PairingAsker(client);

        Assert.Null(await asker.JoinAsync("id", owner.Status() with { Shown = true }, CancellationToken.None));
        Assert.Equal(0, owner.Calls);
    }
}
