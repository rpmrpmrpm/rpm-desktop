using RPM.Core.Sharing;

namespace RPM.Core.Tests;

/// <summary>
/// The pairing board on its own, with a clock moved by hand. Each test is one ordering of events
/// between two computers, chosen because the ordering is easy to get wrong.
/// </summary>
public class PairingBoardTests
{
    private static readonly PairingTimes Times = new(
        Life: TimeSpan.FromMinutes(2),
        Tries: 3,
        Hold: TimeSpan.FromSeconds(5),
        Lease: TimeSpan.FromSeconds(10),
        LostAfter: TimeSpan.FromSeconds(15),
        CrossedPatience: TimeSpan.FromSeconds(10),
        Keep: TimeSpan.FromMinutes(1),
        WrongCodesAllowed: 10,
        WrongCodeWindow: TimeSpan.FromMinutes(10),
        RestFor: TimeSpan.FromMinutes(10));

    // "A" sorts before "B", so A is the one that asks when both do.
    private const string A = "AAAA";
    private const string B = "BBBB";

    private DateTime _now = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

    private PairingBoard Board() => new(Times, () => _now);

    private static PairingPeer Peer(string id) => new(id, id + "-name", "", [1, 2, 3], "");

    // The asking computer's key, as Peer() carries it, and this computer's.
    private static readonly byte[] AskerKey = [1, 2, 3];
    private static readonly byte[] OwnerKey = [4, 5, 6];

    private static PairingSecrets Secrets() => new(AskerKey, OwnerKey);

    /// <summary>Computer <paramref name="them"/> asks <paramref name="me"/>, and sends its nonce for
    /// the first round, so the digits are on screen: a request as it is a moment after it
    /// arrives.</summary>
    private static (PairingBoard.IncomingKind Kind, Pairing? Pairing) Asked(
        PairingBoard board, string me, string them, bool roomHere)
    {
        var (kind, pairing) = board.Incoming(me, Peer(them), Secrets(), roomHere);
        if (pairing is null)
            return (kind, null);

        board.Join(pairing.Id, 1, PairingCode.Nonce());
        return (kind, board.Snapshot().First(p => p.Id == pairing.Id));
    }

    /// <summary>The asking computer's two steps for a typed code, without its own check: commit to
    /// it, then open. This is what the board sees when a user types.</summary>
    private static (PairStatus? Status, Pairing? Accepted) Type(PairingBoard board, string id, string code, bool roomHere = true)
    {
        var round = board.Status(id)?.Round ?? 1;
        var opening = PairingCode.Nonce();
        board.Commit(id, round, PairingCode.CommitCode(AskerKey, OwnerKey, opening, code));
        return board.Open(id, round, opening, roomHere);
    }

    /// <summary>The digits on this screen for that pairing now. Sends the asking computer's nonce
    /// first when the round is still waiting for it, as the asking computer does when it sees a new
    /// round.</summary>
    private static string Right(PairingBoard board, string id)
    {
        if (board.Status(id) is { State: PairStatusKind.ShowingCode, Shown: false } status)
            board.Join(id, status.Round, PairingCode.Nonce());

        return board.Snapshot().FirstOrDefault(p => p.Id == id)?.Code ?? "";
    }

    /// <summary>Digits that are not the ones on this screen.</summary>
    private static string Wrong(PairingBoard board, string id) =>
        Right(board, id) is { Length: 6 } right
            ? ((int.Parse(right, System.Globalization.CultureInfo.InvariantCulture) + 1) % 1_000_000)
                .ToString("D6", System.Globalization.CultureInfo.InvariantCulture)
            : "000000";

    private static PairStatus Showing(long rev = 1, int tries = 3, int expiresIn = 120) =>
        new() { State = PairStatusKind.ShowingCode, Rev = rev, TriesLeft = tries, ExpiresIn = expiresIn };

    // ── one live pairing per computer ──

    [Fact]
    public void Asking_twice_returns_the_pairing_already_standing()
    {
        var board = Board();

        var (first, started) = board.Begin(Peer(B));
        var (second, again) = board.Begin(Peer(B));

        Assert.True(started);
        Assert.False(again);
        Assert.Equal(first.Attempt, second.Attempt);
        Assert.Single(board.Snapshot());
    }

    [Fact]
    public void Asking_does_not_replace_a_question_from_that_computer()
    {
        var board = Board();
        Asked(board, A, B, true);

        var (pairing, started) = board.Begin(Peer(B));

        Assert.False(started);
        Assert.Equal(PairingState.ShowingCode, pairing.State);
    }

    // ── crossed requests ──

    [Fact]
    public void Crossed_this_computer_asks_first_so_their_request_is_refused()
    {
        var board = Board();
        var (mine, _) = board.Begin(Peer(B));

        var (kind, created) = Asked(board, A, B, true);

        Assert.Equal(PairingBoard.IncomingKind.Crossed, kind);
        Assert.Null(created);
        Assert.Equal(mine.Attempt, board.Find(B)!.Attempt);
        Assert.Equal(PairingState.Sending, board.Find(B)!.State);
    }

    [Fact]
    public void Crossed_their_request_first_then_the_409_shows_the_code_and_ignores_the_409()
    {
        var board = Board();
        var (mine, _) = board.Begin(Peer(A));

        // B is this computer; A asks first. Their request arrives before the 409 for ours.
        var (kind, _) = Asked(board, B, A, true);
        board.Crossed(A, mine.Attempt);

        Assert.Equal(PairingBoard.IncomingKind.Created, kind);
        Assert.Equal(PairingState.ShowingCode, board.Find(A)!.State);
        Assert.False(board.IsCurrent(A, mine.Attempt));
    }

    [Fact]
    public void Crossed_the_409_first_then_their_request_shows_the_code()
    {
        var board = Board();
        var (mine, _) = board.Begin(Peer(A));

        board.Crossed(A, mine.Attempt);
        Assert.Equal(PairingState.CrossedWaiting, board.Find(A)!.State);

        Asked(board, B, A, true);

        Assert.Equal(PairingState.ShowingCode, board.Find(A)!.State);
    }

    [Fact]
    public void Crossed_and_their_request_never_comes_ends_as_unreachable()
    {
        var board = Board();
        var (mine, _) = board.Begin(Peer(A));
        board.Crossed(A, mine.Attempt);

        _now += TimeSpan.FromSeconds(9);
        board.Sweep();
        Assert.Equal(PairingState.CrossedWaiting, board.Find(A)!.State);

        _now += TimeSpan.FromSeconds(1);
        board.Sweep();
        Assert.Equal(PairingState.Unreachable, board.Find(A)!.State);
    }

    // ── the first ending wins ──

    [Fact]
    public void A_right_code_then_decline_stays_accepted()
    {
        var board = Board();
        var (_, asked) = Asked(board, A, B, true);

        var (status, accepted) = Type(board, asked!.Id, Right(board, asked!.Id), true);
        board.Decline(B);

        Assert.Equal(PairStatusKind.Accepted, status!.State);
        Assert.NotNull(accepted);
        Assert.Equal(PairStatusKind.Accepted, board.Status(asked.Id)!.State);
    }

    [Fact]
    public void Decline_then_a_right_code_stays_declined_and_records_nobody()
    {
        var board = Board();
        var (_, asked) = Asked(board, A, B, true);

        board.Decline(B);
        var (status, accepted) = Type(board, asked!.Id, Right(board, asked!.Id), true);

        Assert.Equal(PairStatusKind.Declined, status!.State);
        Assert.Null(accepted);
    }

    [Fact]
    public void Withdraw_then_a_right_code_stays_withdrawn()
    {
        var board = Board();
        var (_, asked) = Asked(board, A, B, true);

        board.WithdrawIncoming(asked!.Id);
        var (status, accepted) = Type(board, asked.Id, Right(board, asked.Id), true);

        Assert.Equal(PairStatusKind.Withdrawn, status!.State);
        Assert.Null(accepted);
    }

    [Fact]
    public void A_right_code_then_withdraw_stays_accepted()
    {
        var board = Board();
        var (_, asked) = Asked(board, A, B, true);

        Type(board, asked!.Id, Right(board, asked!.Id), true);
        board.WithdrawIncoming(asked.Id);

        Assert.Equal(PairStatusKind.Accepted, board.Status(asked.Id)!.State);
    }

    // ── expiry is a fact, not a timer ──

    [Fact]
    public void A_code_after_the_expiry_is_expired_even_if_the_sweep_has_not_run()
    {
        var board = Board();
        var (_, asked) = Asked(board, A, B, true);

        _now += Times.Life;
        var (status, accepted) = Type(board, asked!.Id, Right(board, asked!.Id), true);

        Assert.Equal(PairStatusKind.Expired, status!.State);
        Assert.Null(accepted);
        Assert.Equal(PairingState.Expired, board.Find(B)!.State);
    }

    [Fact]
    public void Time_left_counts_down_on_the_wire()
    {
        var board = Board();
        var (_, asked) = Asked(board, A, B, true);

        _now += TimeSpan.FromSeconds(30);

        Assert.Equal(90, board.Status(asked!.Id)!.ExpiresIn);
    }

    // ── tries ──

    [Fact]
    public void Three_wrong_codes_end_it_and_the_right_one_is_worth_nothing_after()
    {
        var board = Board();
        var (_, asked) = Asked(board, A, B, true);

        Assert.Equal(2, Type(board, asked!.Id, Wrong(board, asked!.Id), true).Status!.TriesLeft);
        Assert.True(board.Find(B)!.WrongCode);
        Assert.Equal(1, Type(board, asked.Id, Wrong(board, asked.Id), true).Status!.TriesLeft);
        Assert.Equal(PairStatusKind.TooManyTries, Type(board, asked.Id, Wrong(board, asked.Id), true).Status!.State);

        var (status, accepted) = Type(board, asked.Id, Right(board, asked.Id), true);
        Assert.Equal(PairStatusKind.TooManyTries, status!.State);
        Assert.Null(accepted);
    }

    // ── full ──

    [Fact]
    public void A_computer_with_no_room_says_full_and_shows_nothing()
    {
        var board = Board();

        var (kind, created) = Asked(board, A, B, false);

        Assert.Equal(PairingBoard.IncomingKind.Full, kind);
        Assert.Null(created);
        Assert.Empty(board.Snapshot());
    }

    [Fact]
    public void A_place_taken_while_the_code_was_walked_over_ends_full()
    {
        var board = Board();
        var (_, asked) = Asked(board, A, B, true);

        var (status, accepted) = Type(board, asked!.Id, Right(board, asked!.Id), false);

        Assert.Equal(PairStatusKind.Full, status!.State);
        Assert.Null(accepted);
    }

    // ── following ──

    [Fact]
    public void Nobody_following_ends_it_as_lost_after_the_lease()
    {
        var board = Board();
        Asked(board, A, B, true);

        _now += Times.Lease - TimeSpan.FromSeconds(1);
        board.Sweep();
        Assert.Equal(PairingState.ShowingCode, board.Find(B)!.State);

        _now += TimeSpan.FromSeconds(1);
        board.Sweep();
        Assert.Equal(PairingState.Lost, board.Find(B)!.State);
    }

    [Fact]
    public async Task A_held_status_call_keeps_it_alive_and_the_lease_starts_when_it_ends()
    {
        var board = Board();
        var (_, asked) = Asked(board, A, B, true);

        using var hang = new CancellationTokenSource();
        var held = board.WaitAsync(asked!.Id, rev: asked.Rev, hang.Token);

        _now += Times.Lease * 3;
        board.Sweep();
        Assert.Equal(PairingState.ShowingCode, board.Find(B)!.State);

        hang.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => held);

        // The lease is measured from the end of the call, not from the request.
        _now += Times.Lease - TimeSpan.FromSeconds(1);
        board.Sweep();
        Assert.Equal(PairingState.ShowingCode, board.Find(B)!.State);

        _now += TimeSpan.FromSeconds(1);
        board.Sweep();
        Assert.Equal(PairingState.Lost, board.Find(B)!.State);
    }

    [Fact]
    public async Task A_held_status_call_answers_when_something_changes()
    {
        var board = Board();
        var (_, asked) = Asked(board, A, B, true);

        var held = board.WaitAsync(asked!.Id, rev: asked.Rev, CancellationToken.None);
        Assert.False(held.IsCompleted);

        board.Commit(asked.Id, round: 1, PairingCode.CommitCode(AskerKey, OwnerKey, PairingCode.Nonce(), "000000"));

        var status = await held.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(PairStatusKind.ShowingCode, status!.State);
        Assert.NotEqual("", status.Reveal);
        Assert.NotEqual(asked.Rev, status.Rev);
    }

    [Fact]
    public async Task A_held_status_call_answers_at_once_when_the_revision_already_moved()
    {
        var board = Board();
        var (_, asked) = Asked(board, A, B, true);
        board.Decline(B);

        var status = await board.WaitAsync(asked!.Id, rev: asked.Rev, CancellationToken.None)
                                .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(PairStatusKind.Declined, status!.State);
    }

    [Fact]
    public async Task An_unknown_id_is_answered_with_nothing()
    {
        var board = Board();

        Assert.Null(board.Status("nope"));
        Assert.Null(await board.WaitAsync("nope", 1, CancellationToken.None));
        Assert.Null(board.Commit("nope", 1, [1]));
        Assert.Null(board.Open("nope", 1, [1], true).Status);
    }

    [Fact]
    public void Stopping_the_server_ends_every_code_on_screen()
    {
        var board = Board();
        Asked(board, A, B, true);

        board.EndAsked();

        Assert.Equal(PairingState.Lost, board.Find(B)!.State);
    }

    // ── the asking side's copy ──

    [Fact]
    public void A_reply_for_a_replaced_attempt_changes_nothing()
    {
        var board = Board();
        var (first, _) = board.Begin(Peer(B));
        board.Offered(B, first.Attempt, "id-1", Showing());
        board.Mirror(B, first.Attempt, new PairStatus { State = PairStatusKind.Declined, Rev = 2 });

        // Ask again. The old attempt's status call is still open and answers late with Accepted.
        var (second, started) = board.Begin(Peer(B));
        var late = board.Mirror(B, first.Attempt, new PairStatus { State = PairStatusKind.Accepted, Rev = 3 });

        Assert.True(started);
        Assert.Null(late);
        Assert.Equal(PairingState.Sending, board.Find(B)!.State);
        Assert.Equal(second.Attempt, board.Find(B)!.Attempt);
    }

    [Fact]
    public void Accepted_is_handed_out_once_however_many_replies_say_it()
    {
        var board = Board();
        var (mine, _) = board.Begin(Peer(B));
        board.Offered(B, mine.Attempt, "id-1", Showing());

        var accepted = new PairStatus { State = PairStatusKind.Accepted, Rev = 2 };
        var fromCodeReply = board.Mirror(B, mine.Attempt, accepted);
        var fromStatusReply = board.Mirror(B, mine.Attempt, accepted);

        Assert.NotNull(fromCodeReply);
        Assert.Null(fromStatusReply);
    }

    [Fact]
    public void A_wrong_code_on_the_asking_side_is_seen_from_the_tries()
    {
        var board = Board();
        var (mine, _) = board.Begin(Peer(B));
        board.Offered(B, mine.Attempt, "id-1", Showing());

        board.Mirror(B, mine.Attempt, Showing(rev: 2, tries: 2, expiresIn: 100));

        var pairing = board.Find(B)!;
        Assert.Equal(PairingState.WaitingForCode, pairing.State);
        Assert.True(pairing.WrongCode);
        Assert.Equal(2, pairing.TriesLeft);
    }

    [Fact]
    public void Gone_on_the_asking_side_reads_as_lost()
    {
        var board = Board();
        var (mine, _) = board.Begin(Peer(B));
        board.Offered(B, mine.Attempt, "id-1", Showing());

        board.Mirror(B, mine.Attempt, PairStatus.Gone);

        Assert.Equal(PairingState.Lost, board.Find(B)!.State);
    }

    [Fact]
    public void Cancel_removes_the_request_and_hands_back_its_id()
    {
        var board = Board();
        var (mine, _) = board.Begin(Peer(B));
        board.Offered(B, mine.Attempt, "id-1", Showing());

        var removed = board.Withdraw(B);

        Assert.Equal("id-1", removed!.Id);
        Assert.Null(board.Find(B));
        Assert.False(board.IsCurrent(B, mine.Attempt));
    }

    // ── dismissing ──

    [Fact]
    public void Dismissing_on_the_asking_side_removes_the_row()
    {
        var board = Board();
        var (mine, _) = board.Begin(Peer(B));
        board.Undelivered(B, mine.Attempt);

        board.Dismiss(B);

        Assert.Null(board.Find(B));
    }

    [Fact]
    public void A_declined_question_stays_readable_on_the_wire_for_the_keep()
    {
        var board = Board();
        var (_, asked) = Asked(board, A, B, true);
        board.Decline(B);

        Assert.True(board.Find(B)!.Dismissed);

        _now += Times.Keep - TimeSpan.FromSeconds(1);
        board.Sweep();
        Assert.Equal(PairStatusKind.Declined, board.Status(asked!.Id)!.State);

        _now += TimeSpan.FromSeconds(1);
        board.Sweep();
        Assert.Null(board.Status(asked.Id));
        Assert.Null(board.Find(B));
    }

    [Fact]
    public void An_ending_nobody_dismissed_stays_on_screen()
    {
        var board = Board();
        Asked(board, A, B, true);

        _now += Times.Life;
        board.Sweep();
        _now += Times.Keep * 10;
        board.Sweep();

        Assert.Equal(PairingState.Expired, board.Find(B)!.State);
        Assert.False(board.Find(B)!.Dismissed);
    }

    [Fact]
    public void The_same_computer_asking_again_replaces_its_old_question()
    {
        var board = Board();
        var (_, first) = Asked(board, A, B, true);
        var (_, second) = Asked(board, A, B, true);

        Assert.NotEqual(first!.Id, second!.Id);
        Assert.Equal(PairStatusKind.Withdrawn, board.Status(first.Id)!.State);
        Assert.Equal(second.Code, board.Find(B)!.Code);
    }

    // ── what needs the user ──

    [Fact]
    public void Needs_attention_follows_what_is_on_the_board()
    {
        var board = Board();
        Assert.False(board.NeedsAttention);

        var (_, asked) = Asked(board, A, B, true);
        Assert.True(board.NeedsAttention);

        Type(board, asked!.Id, Right(board, asked!.Id), true);
        Assert.False(board.NeedsAttention);

        var (mine, _) = board.Begin(Peer("CCCC"));
        Assert.False(board.NeedsAttention);

        board.Undelivered("CCCC", mine.Attempt);
        Assert.True(board.NeedsAttention);

        board.Dismiss("CCCC");
        Assert.False(board.NeedsAttention);
    }

    [Fact]
    public void Every_change_is_announced()
    {
        var board = Board();
        int raised = 0;
        board.Changed += () => raised++;

        var (mine, _) = board.Begin(Peer(B));
        board.Offered(B, mine.Attempt, "id-1", Showing());
        board.Mirror(B, mine.Attempt, Showing());   // nothing new, so not announced
        board.Lose(B, mine.Attempt);

        Assert.Equal(3, raised);
    }

    // ── the exchange, round by round ──

    [Fact]
    public void The_digits_go_on_screen_only_with_the_asking_computers_nonce()
    {
        var board = Board();
        var (_, asked) = board.Incoming(A, Peer(B), Secrets(), roomHere: true);

        var before = board.Status(asked!.Id)!;
        Assert.Equal(1, before.Round);
        Assert.NotEqual("", before.Commit);
        Assert.False(before.Shown);
        Assert.Equal("", board.Find(B)!.Code);

        var after = board.Join(asked.Id, 1, PairingCode.Nonce())!;

        Assert.True(after.Shown);
        Assert.Equal(before.Commit, after.Commit);
        Assert.Matches("^[0-9]{6}$", board.Find(B)!.Code);
    }

    [Fact]
    public void The_digits_are_made_from_both_keys_and_both_nonces_and_the_revealed_nonce_is_the_committed_one()
    {
        var board = Board();
        var (_, asked) = board.Incoming(A, Peer(B), Secrets(), roomHere: true);
        var askerNonce = PairingCode.Nonce();
        board.Join(asked!.Id, 1, askerNonce);

        var status = board.Commit(asked.Id, 1, [9])!;
        var ownerNonce = SharingProtocol.FromBase64Url(status.Reveal)!;

        Assert.Equal(SharingProtocol.ToBase64Url(PairingCode.CommitNonce(ownerNonce)), status.Commit);
        Assert.Equal(PairingCode.For(AskerKey, OwnerKey, askerNonce, ownerNonce), board.Find(B)!.Code);
    }

    [Fact]
    public void A_second_nonce_for_the_same_round_changes_nothing()
    {
        var board = Board();
        var (_, asked) = Asked(board, A, B, true);
        var rev = board.Status(asked!.Id)!.Rev;

        board.Join(asked.Id, 1, PairingCode.Nonce());

        Assert.Equal(asked.Code, board.Find(B)!.Code);
        Assert.Equal(rev, board.Status(asked.Id)!.Rev);
    }

    [Fact]
    public void A_commitment_before_the_digits_are_shown_is_not_taken()
    {
        var board = Board();
        var (_, asked) = board.Incoming(A, Peer(B), Secrets(), roomHere: true);

        Assert.Equal("", board.Commit(asked!.Id, 1, [9])!.Reveal);

        board.Join(asked.Id, 1, PairingCode.Nonce());
        Assert.NotEqual("", board.Commit(asked.Id, 1, [9])!.Reveal);
    }

    [Fact]
    public void The_nonce_is_revealed_only_after_a_commitment()
    {
        var board = Board();
        var (_, asked) = Asked(board, A, B, true);

        Assert.Equal("", board.Status(asked!.Id)!.Reveal);
        Assert.NotEqual("", board.Commit(asked.Id, 1, [9])!.Reveal);
        Assert.NotEqual("", board.Status(asked.Id)!.Reveal);
    }

    /// <summary>
    /// After the nonce is revealed, anybody who has it can compute the digits. A second commitment
    /// made then, with the right digits, must not be what the opening is checked against.
    /// </summary>
    [Fact]
    public void Only_the_first_commitment_of_a_round_counts()
    {
        var board = Board();
        var (_, asked) = Asked(board, A, B, true);
        board.Commit(asked!.Id, 1, [9]);

        var opening = PairingCode.Nonce();
        board.Commit(asked.Id, 1, PairingCode.CommitCode(AskerKey, OwnerKey, opening, asked.Code));
        var (status, accepted) = board.Open(asked.Id, 1, opening, roomHere: true);

        Assert.Null(accepted);
        Assert.Equal(2, status!.Round);
        Assert.True(board.Find(B)!.WrongCode);
    }

    [Fact]
    public void A_wrong_code_starts_a_new_round_whose_digits_wait_for_the_asking_computer()
    {
        var board = Board();
        var (_, asked) = Asked(board, A, B, true);
        var first = board.Status(asked!.Id)!;

        var (status, _) = Type(board, asked.Id, Wrong(board, asked.Id));

        Assert.Equal(2, status!.Round);
        Assert.NotEqual(first.Commit, status.Commit);
        Assert.False(status.Shown);
        Assert.Equal("", status.Reveal);
        Assert.Equal(PairingState.ShowingCode, board.Find(B)!.State);
        Assert.Equal("", board.Find(B)!.Code);

        // The asking computer joins the new round and the right digits for it are accepted.
        var (_, accepted) = Type(board, asked.Id, Right(board, asked.Id));
        Assert.NotNull(accepted);
    }

    [Fact]
    public void An_empty_opening_counts_as_a_wrong_code()
    {
        var board = Board();
        var (_, asked) = Asked(board, A, B, true);
        board.Commit(asked!.Id, 1, PairingCode.CommitCode(AskerKey, OwnerKey, PairingCode.Nonce(), asked.Code));

        var (status, accepted) = board.Open(asked.Id, 1, [], roomHere: true);

        Assert.Null(accepted);
        Assert.Equal(2, status!.TriesLeft);
        Assert.Equal(2, status.Round);
    }

    [Fact]
    public void An_opening_for_a_round_that_has_passed_is_ignored()
    {
        var board = Board();
        var (_, asked) = Asked(board, A, B, true);
        var opening = PairingCode.Nonce();
        board.Commit(asked!.Id, 1, PairingCode.CommitCode(AskerKey, OwnerKey, opening, asked.Code));
        board.Open(asked.Id, 1, [], roomHere: true);

        var (status, accepted) = board.Open(asked.Id, 1, opening, roomHere: true);

        Assert.Null(accepted);
        Assert.Equal(2, status!.Round);
        Assert.Equal(2, status.TriesLeft);
    }

    /// <summary>Each rest in a row is twice as long as the one before. With a fixed rest a machine could keep
    /// guessing at about one code a minute for as long as it liked.</summary>
    [Fact]
    public void A_second_rest_in_a_row_is_twice_as_long()
    {
        var board = new PairingBoard(Times with { WrongCodesAllowed = 3 }, () => _now);

        void ThreeWrongCodes()
        {
            var (_, asked) = Asked(board, A, B, true);
            for (var i = 0; i < 3; i++)
                Type(board, asked!.Id, Wrong(board, asked.Id));
        }

        ThreeWrongCodes();
        _now += Times.RestFor;
        ThreeWrongCodes();

        _now += Times.RestFor;
        Assert.Equal(PairingBoard.IncomingKind.Resting, board.Incoming(A, Peer("CCCC"), Secrets(), roomHere: true).Kind);

        _now += Times.RestFor;
        Assert.Equal(PairingBoard.IncomingKind.Created, board.Incoming(A, Peer("CCCC"), Secrets(), roomHere: true).Kind);
    }

    [Fact]
    public void Too_many_wrong_codes_across_requests_stop_requests_for_a_while()
    {
        var board = new PairingBoard(Times with { WrongCodesAllowed = 4 }, () => _now);

        // Three wrong codes end the first request; the same computer asks again and gets one more.
        var (_, first) = Asked(board, A, B, true);
        for (var i = 0; i < 3; i++)
            Type(board, first!.Id, Wrong(board, first.Id));
        var (_, second) = Asked(board, A, B, true);
        Type(board, second!.Id, Wrong(board, second.Id));

        Assert.Equal(PairingBoard.IncomingKind.Resting, board.Incoming(A, Peer("CCCC"), Secrets(), roomHere: true).Kind);
        Assert.Null(board.Find("CCCC"));

        _now += Times.RestFor;

        Assert.Equal(PairingBoard.IncomingKind.Created, board.Incoming(A, Peer("CCCC"), Secrets(), roomHere: true).Kind);
    }

    [Fact]
    public void Wrong_codes_older_than_the_window_are_not_counted()
    {
        var board = new PairingBoard(Times with { WrongCodesAllowed = 2 }, () => _now);

        var (_, first) = Asked(board, A, B, true);
        Type(board, first!.Id, Wrong(board, first.Id));

        _now += Times.WrongCodeWindow;

        var (_, second) = Asked(board, A, B, true);
        Type(board, second!.Id, Wrong(board, second.Id));

        Assert.Equal(PairingBoard.IncomingKind.Created, board.Incoming(A, Peer("CCCC"), Secrets(), roomHere: true).Kind);
    }

    [Fact]
    public void A_resting_answer_on_the_asking_side_reads_as_resting()
    {
        var board = Board();
        var (mine, _) = board.Begin(Peer(B));

        board.Offered(B, mine.Attempt, "", new PairStatus { State = PairStatusKind.Resting });

        Assert.Equal(PairingState.Resting, board.Find(B)!.State);
    }

    [Fact]
    public void An_older_computer_on_the_asking_side_reads_as_needing_an_update()
    {
        var board = Board();
        var (mine, _) = board.Begin(Peer(B));

        board.Outdated(B, mine.Attempt);

        Assert.Equal(PairingState.NeedsUpdateThere, board.Find(B)!.State);
        Assert.False(board.Find(B)!.IsLive);
    }
}
