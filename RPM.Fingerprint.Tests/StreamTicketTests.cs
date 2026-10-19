namespace RPM.Fingerprint.Tests;

/// <summary>
/// The note that says a live transcode may be served.
///
/// <para>A Lambda function URL authenticates with SigV4 or with nothing at all, and a phone cannot
/// sign SigV4. So the URL is open, and this is what stands in front of it. Each of the checks below
/// guards against something that would otherwise give access to somebody else's music, or to an
/// encoder that anyone can run for free.</para>
/// </summary>
public class StreamTicketTests
{
    private const string Sha = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private const string Secret = "a-secret-only-the-two-functions-have";

    private static readonly DateTimeOffset Now = new(2026, 8, 30, 22, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Reads_back_what_was_written()
    {
        var token = StreamTicket.Mint(Sha, 320, Now.AddMinutes(5), Secret);
        var read = StreamTicket.Read(token, Secret, Now);

        Assert.NotNull(read);
        Assert.Equal(Sha, read.Sha256);
        Assert.Equal(320, read.Kilobits);
    }

    /// <summary>The whole point. A rate is what the encoder is told to make and what the account was
    /// judged against, so a listener who can edit it has bought nothing.</summary>
    [Fact]
    public void Refuses_a_rate_somebody_edited()
    {
        var token = StreamTicket.Mint(Sha, 128, Now.AddMinutes(5), Secret);
        var raised = token.Replace(".128.", ".320.", StringComparison.Ordinal);

        Assert.NotEqual(token, raised);
        Assert.Null(StreamTicket.Read(raised, Secret, Now));
    }

    /// <summary>Same reason, one field along: the hash names the object, so an edited hash is a
    /// request for a record this listener was never granted.</summary>
    [Fact]
    public void Refuses_a_hash_somebody_edited()
    {
        var token = StreamTicket.Mint(Sha, 320, Now.AddMinutes(5), Secret);
        var elsewhere = token.Replace(Sha, new string('a', 64), StringComparison.Ordinal);

        Assert.Null(StreamTicket.Read(elsewhere, Secret, Now));
    }

    [Fact]
    public void Refuses_one_that_has_run_out()
    {
        var token = StreamTicket.Mint(Sha, 320, Now.AddMinutes(5), Secret);

        Assert.NotNull(StreamTicket.Read(token, Secret, Now.AddMinutes(4)));
        Assert.Null(StreamTicket.Read(token, Secret, Now.AddMinutes(6)));
    }

    [Fact]
    public void Refuses_one_signed_with_another_secret()
    {
        var token = StreamTicket.Mint(Sha, 320, Now.AddMinutes(5), Secret);

        Assert.Null(StreamTicket.Read(token, "some-other-secret", Now));
    }

    [Fact]
    public void Refuses_anything_that_is_not_a_ticket()
    {
        foreach (var nonsense in new[] { "", "   ", "v1", "v1.a.b.c.d", "....", Sha })
            Assert.Null(StreamTicket.Read(nonsense, Secret, Now));
    }

    /// <summary>A rate off the ladder is a path that does not exist, such as <c>rate/160/…</c>. The
    /// place to refuse it is where the note is written, not where it is read.</summary>
    [Fact]
    public void Will_not_write_a_note_for_a_rate_that_is_not_made()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => StreamTicket.Mint(Sha, 160, Now.AddMinutes(5), Secret));

        // The original is not a rendition either: it is served straight off the blob and no encoder
        // is ever run for it.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => StreamTicket.Mint(Sha, AudioLadder.Original, Now.AddMinutes(5), Secret));
    }

    /// <summary>A token is a query-string value and a path segment on the way through CloudFront. If
    /// it needs escaping somewhere, it will be unescaped somewhere else and stop verifying.</summary>
    [Fact]
    public void Is_safe_to_put_in_a_url_as_it_stands()
    {
        var token = StreamTicket.Mint(Sha, 320, Now.AddMinutes(5), Secret);

        Assert.Equal(token, Uri.EscapeDataString(token));
    }

    /// <summary>Two notes for the same track at the same rate are not interchangeable across
    /// secrets, and a secret that has been rotated invalidates every note written under the old
    /// one. Which is what makes rotation a thing that can be done at all.</summary>
    [Fact]
    public void Signs_differently_under_a_different_secret()
    {
        var one = StreamTicket.Mint(Sha, 320, Now.AddMinutes(5), Secret);
        var other = StreamTicket.Mint(Sha, 320, Now.AddMinutes(5), "another-secret");

        Assert.NotEqual(one, other);
    }
}
