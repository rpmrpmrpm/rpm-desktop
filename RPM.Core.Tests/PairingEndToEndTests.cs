using System.Net.Http;
using System.Net.Http.Json;
using RPM.Core.Sharing;

namespace RPM.Core.Tests;

/// <summary>
/// Two computers in one process: two servers, two boards, real TLS over loopback. What both screens
/// show is read from the two boards, which is exactly what the two apps display.
/// </summary>
public class PairingEndToEndTests : IDisposable
{
    private static readonly PairingTimes Times = new(
        Life: TimeSpan.FromMinutes(2),
        Tries: 3,
        Hold: TimeSpan.FromMilliseconds(500),
        Lease: TimeSpan.FromMilliseconds(400),
        LostAfter: TimeSpan.FromMilliseconds(800),
        CrossedPatience: TimeSpan.FromSeconds(2),
        Keep: TimeSpan.FromMinutes(1),
        WrongCodesAllowed: 10,
        WrongCodeWindow: TimeSpan.FromMinutes(10),
        RestFor: TimeSpan.FromMinutes(10));

    private readonly List<IDisposable> _owned = [];

    private sealed class Computer : IDisposable
    {
        private readonly object _gate = new();
        private readonly List<Pairing> _recorded = [];

        public Computer(PeerIdentity identity, string name)
        {
            Identity = identity;
            Name = name;
            Board = new PairingBoard(Times, () => DateTime.UtcNow);
            Server = new LibraryServer(identity, Board)
            {
                Name = name,
                HasRoom = () => true,
                OnPaired = Record,
            };
            Server.Start();
        }

        public PeerIdentity Identity { get; }
        public string Name { get; }
        public PairingBoard Board { get; }
        public LibraryServer Server { get; }

        /// <summary>Every pairing this computer recorded, from either side.</summary>
        public IReadOnlyList<Pairing> Recorded
        {
            get { lock (_gate) return [.. _recorded]; }
        }

        public void Record(Pairing pairing)
        {
            lock (_gate)
                _recorded.Add(pairing);
        }

        public PairingPeer AsPeer => new(Identity.DeviceId, Name, "", Identity.PublicKey, "");

        public PairingState? StateWith(Computer other) => Board.Find(other.Identity.DeviceId)?.State;

        public void Dispose() => Server.Dispose();
    }

    private Computer Make(PeerIdentity identity, string name)
    {
        var computer = new Computer(identity, name);
        _owned.Add(computer);
        return computer;
    }

    /// <summary>Two computers, the first of which is the one the crossed-request rule lets ask.</summary>
    private (Computer First, Computer Second) Pair()
    {
        var x = PeerIdentity.Create();
        var y = PeerIdentity.Create();
        return PairingCode.AsksFirst(x.DeviceId, y.DeviceId)
            ? (Make(x, "first"), Make(y, "second"))
            : (Make(y, "first"), Make(x, "second"));
    }

    /// <summary>Starts asking, as the app does: on the board first, then the request.</summary>
    private PairingRun Ask(Computer from, Computer to, HttpMessageHandler? handler = null)
    {
        var (pairing, started) = from.Board.Begin(to.AsPeer);
        Assert.True(started);

        var client = new PeerClient(
            from.Identity, "127.0.0.1", to.Server.Port, to.Identity.PublicKey,
            handler ?? PeerCertificate.Handler(to.Identity.PublicKey));

        var run = new PairingRun(from.Board, client, pairing, () => DateTime.UtcNow, from.Record);
        _owned.Add(run);
        _ = run.RunAsync(from.Name, "", "");
        return run;
    }

    /// <summary>The digits on <paramref name="asked"/>'s screen for <paramref name="asking"/>'s request,
    /// after the asking computer's nonce for the round has put them there.</summary>
    private static async Task<string> CodeAsync(Computer asked, Computer asking)
    {
        await Eventually(() => asked.Board.Find(asking.Identity.DeviceId) is { State: PairingState.ShowingCode, Code.Length: 6 });
        return asked.Board.Find(asking.Identity.DeviceId)!.Code;
    }

    /// <summary>Waits for a condition, sweeping both boards as the app's timer does.</summary>
    private static async Task Eventually(Func<bool> condition, params Computer[] sweep)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < until)
        {
            foreach (var computer in sweep)
                computer.Board.Sweep();

            if (condition())
                return;

            await Task.Delay(20);
        }

        Assert.True(condition(), "the condition did not hold within five seconds");
    }

    [Fact]
    public async Task Typing_the_code_pairs_both_computers_and_each_writes_the_other_down_once()
    {
        var (asking, asked) = Pair();
        var run = Ask(asking, asked);

        await Eventually(() => asking.StateWith(asked) is PairingState.WaitingForCode);
        Assert.Equal(PairingState.ShowingCode, asked.StateWith(asking));

        Assert.True(await run.SubmitAsync(await CodeAsync(asked, asking)));

        await Eventually(() => asking.StateWith(asked) is PairingState.Accepted);
        Assert.Equal(PairingState.Accepted, asked.StateWith(asking));

        // Give the status call time to report Accepted a second time; it must not be recorded twice.
        await Task.Delay(300);
        Assert.Single(asking.Recorded);
        Assert.Single(asked.Recorded);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Both_asking_at_once_leaves_one_code_on_one_screen(bool firstPressesFirst)
    {
        var (first, second) = Pair();

        // Both register before either request is answered, which is the case the rule exists for.
        PairingRun runFirst, runSecond;
        if (firstPressesFirst)
        {
            runFirst = Ask(first, second);
            runSecond = Ask(second, first);
        }
        else
        {
            runSecond = Ask(second, first);
            runFirst = Ask(first, second);
        }

        // The smaller id asks. Its screen has the input box; the other screen shows the code.
        await Eventually(() =>
            first.StateWith(second) is PairingState.WaitingForCode &&
            second.StateWith(first) is PairingState.ShowingCode, first, second);

        Assert.True(await runFirst.SubmitAsync(await CodeAsync(second, first)));

        await Eventually(() =>
            first.StateWith(second) is PairingState.Accepted &&
            second.StateWith(first) is PairingState.Accepted);
        _ = runSecond;
    }

    [Fact]
    public async Task A_lost_accepted_reply_is_recovered_from_the_status_call()
    {
        var (asking, asked) = Pair();
        var run = Ask(asking, asked, new DropOpenReplies(PeerCertificate.Handler(asked.Identity.PublicKey)));

        await Eventually(() => asking.StateWith(asked) is PairingState.WaitingForCode);

        // The asked computer accepts, and the reply never arrives.
        Assert.False(await run.SubmitAsync(await CodeAsync(asked, asking)));
        Assert.Equal(PairingState.Accepted, asked.StateWith(asking));

        // The status call reads it instead, and this side records the other computer too.
        await Eventually(() => asking.StateWith(asked) is PairingState.Accepted);
        Assert.Single(asking.Recorded);
        Assert.Single(asked.Recorded);
    }

    [Fact]
    public async Task A_wrong_code_puts_new_digits_on_the_other_screen_and_those_pair()
    {
        var (asking, asked) = Pair();
        var run = Ask(asking, asked);
        await Eventually(() => asking.StateWith(asked) is PairingState.WaitingForCode);

        var first = await CodeAsync(asked, asking);
        var wrong = ((int.Parse(first, System.Globalization.CultureInfo.InvariantCulture) + 1) % 1_000_000)
            .ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(await run.SubmitAsync(wrong));

        // Still asking, told the code was wrong, and the other screen shows the next round's digits.
        Assert.Equal(PairingState.WaitingForCode, asking.StateWith(asked));
        Assert.True(asking.Board.Find(asked.Identity.DeviceId)!.WrongCode);
        Assert.Equal(PairingState.ShowingCode, asked.StateWith(asking));

        Assert.True(await run.SubmitAsync(await CodeAsync(asked, asking)));

        await Eventually(() => asking.StateWith(asked) is PairingState.Accepted);
        Assert.Equal(PairingState.Accepted, asked.StateWith(asking));
    }

    /// <summary>A computer that does not speak pairing 2 answers with an offer that has no commitment.
    /// Its exchange sends the code in the clear, so the asking side does not continue, and withdraws
    /// the request so the code is removed from that screen.</summary>
    [Fact]
    public async Task An_offer_without_a_commitment_is_an_older_computer_and_is_not_paired_with()
    {
        var (asking, asked) = Pair();
        Ask(asking, asked, new OfferWithoutCommitment(PeerCertificate.Handler(asked.Identity.PublicKey)));

        await Eventually(() => asking.StateWith(asked) is PairingState.NeedsUpdateThere);
        await Eventually(() => asked.StateWith(asking) is PairingState.Withdrawn);
        Assert.Empty(asking.Recorded);
        Assert.Empty(asked.Recorded);
    }

    [Fact]
    public async Task Cancel_takes_the_code_off_the_other_screen()
    {
        var (asking, asked) = Pair();
        var run = Ask(asking, asked);
        await Eventually(() => asking.StateWith(asked) is PairingState.WaitingForCode);

        Assert.NotNull(asking.Board.Withdraw(asked.Identity.DeviceId));
        await run.CancelAsync();

        Assert.Equal(PairingState.Withdrawn, asked.StateWith(asking));
        Assert.Null(asking.StateWith(asked));
    }

    [Fact]
    public async Task A_decline_reaches_the_asking_screen_without_a_code_being_typed()
    {
        var (asking, asked) = Pair();
        Ask(asking, asked);
        await Eventually(() => asking.StateWith(asked) is PairingState.WaitingForCode);

        asked.Board.Decline(asking.Identity.DeviceId);

        await Eventually(() => asking.StateWith(asked) is PairingState.Declined);
    }

    [Fact]
    public async Task The_asking_app_quitting_takes_the_code_off_the_other_screen()
    {
        var (asking, asked) = Pair();
        var run = Ask(asking, asked);
        await Eventually(() => asking.StateWith(asked) is PairingState.WaitingForCode);

        // No withdraw: the app is gone. Only the lease can notice.
        run.Dispose();

        await Eventually(() => asked.StateWith(asking) is PairingState.Lost, asked);
    }

    [Fact]
    public async Task The_asked_computer_going_away_ends_the_request_here()
    {
        var (asking, asked) = Pair();
        Ask(asking, asked);
        await Eventually(() => asking.StateWith(asked) is PairingState.WaitingForCode);

        asked.Server.Dispose();

        await Eventually(() => asking.StateWith(asked) is PairingState.Lost);
    }

    [Fact]
    public async Task Ask_again_after_a_decline_puts_a_new_code_on_the_other_screen()
    {
        var (asking, asked) = Pair();
        Ask(asking, asked);
        await Eventually(() => asking.StateWith(asked) is PairingState.WaitingForCode);

        var firstId = asked.Board.Find(asking.Identity.DeviceId)!.Id;
        asked.Board.Decline(asking.Identity.DeviceId);
        await Eventually(() => asking.StateWith(asked) is PairingState.Declined);

        Ask(asking, asked);

        await Eventually(() =>
            asking.StateWith(asked) is PairingState.WaitingForCode &&
            asked.StateWith(asking) is PairingState.ShowingCode);
        Assert.NotEqual(firstId, asked.Board.Find(asking.Identity.DeviceId)!.Id);
    }

    /// <summary>Removes the commitment from the offer, which is what an offer from a computer that
    /// does not speak pairing 2 looks like.</summary>
    private sealed class OfferWithoutCommitment(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (request.RequestUri?.AbsolutePath != SharingProtocol.PairRoute || !response.IsSuccessStatusCode)
                return response;

            var offer = await response.Content.ReadFromJsonAsync(SharingJson.Default.PairOffer, cancellationToken);
            response.Dispose();
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = JsonContent.Create(
                    offer! with { Status = offer.Status with { Commit = "" } }, SharingJson.Default.PairOffer),
            };
        }
    }

    /// <summary>Lets the opening through and loses its reply, as a dropped connection would.</summary>
    private sealed class DropOpenReplies(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (request.RequestUri?.AbsolutePath == SharingProtocol.PairOpenRoute)
            {
                response.Dispose();
                throw new HttpRequestException("the reply was lost");
            }
            return response;
        }
    }

    public void Dispose()
    {
        foreach (var owned in _owned)
            owned.Dispose();
    }
}
