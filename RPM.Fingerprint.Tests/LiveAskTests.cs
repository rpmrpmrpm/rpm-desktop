namespace RPM.Fingerprint.Tests;

/// <summary>
/// What a request at the transcoder's own URL is asking for.
///
/// <para>Everything the function knows about the caller comes from here: it is behind an open
/// function URL, it never sees an account, and this is the whole of its input. So this is where a
/// request stops being a string.</para>
/// </summary>
public class LiveAskTests
{
    private const string Sha = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private const string Secret = "a-secret-only-the-two-functions-have";

    private static readonly DateTimeOffset Now = new(2026, 8, 30, 22, 0, 0, TimeSpan.Zero);

    private static string Token(int kilobits = 320) =>
        StreamTicket.Mint(Sha, kilobits, Now.AddHours(1), Secret);

    [Fact]
    public void Reads_a_track_asked_for_from_the_top()
    {
        var ask = LiveAsk.Read($"/live/{Token()}", from: null, Secret, Now);

        Assert.NotNull(ask);
        Assert.Equal(Sha, ask.Ticket.Sha256);
        Assert.Equal(320, ask.Ticket.Kilobits);
        Assert.Equal(0, ask.From);
    }

    /// <summary>A seek. Seconds rather than a byte range on purpose: the encoder can start anywhere,
    /// and a byte offset would have to be turned back into a second by assuming a constant rate the
    /// encoder never promised.</summary>
    [Fact]
    public void Reads_a_track_asked_for_from_partway_in()
    {
        var ask = LiveAsk.Read($"/live/{Token()}", from: "94.5", Secret, Now);

        Assert.NotNull(ask);
        Assert.Equal(94.5, ask.From);
    }

    /// <summary>The number comes off a wire and is used as an ffmpeg argument. A negative one seeks
    /// backwards from the end, which is not what any caller meant.</summary>
    [Fact]
    public void Reads_a_second_that_is_not_a_second_as_the_top_of_the_track()
    {
        foreach (var nonsense in new[] { "", " ", "soon", "-30", "NaN", "1e400" })
            Assert.Equal(0, LiveAsk.Read($"/live/{Token()}", nonsense, Secret, Now)!.From);
    }

    /// <summary>Written with the invariant point, because it is read back by a function whose culture
    /// is whatever the runtime was started with. "94,5" parsed as 945 is a seek to a place the record
    /// does not reach.</summary>
    [Fact]
    public void Reads_a_second_the_same_way_whatever_the_machine_thinks_a_point_is()
    {
        var was = Thread.CurrentThread.CurrentCulture;

        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("nl-NL");
            Assert.Equal(94.5, LiveAsk.Read($"/live/{Token()}", "94.5", Secret, Now)!.From);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = was;
        }
    }

    [Fact]
    public void Refuses_a_path_that_carries_no_note()
    {
        foreach (var path in new[] { "", "/", "/live", "/live/", "/something-else", "/live/not-a-note" })
            Assert.Null(LiveAsk.Read(path, from: null, Secret, Now));
    }

    /// <summary>The note is the only authorisation there is, so a refused note is a refused
    /// request, not a request for whatever the path happened to spell.</summary>
    [Fact]
    public void Refuses_a_note_that_does_not_verify()
    {
        var expired = StreamTicket.Mint(Sha, 320, Now.AddMinutes(-1), Secret);

        Assert.Null(LiveAsk.Read($"/live/{expired}", from: null, Secret, Now));
        Assert.Null(LiveAsk.Read($"/live/{Token()}", from: null, "another-secret", Now));
    }

    /// <summary>CloudFront and a function URL disagree about the trailing slash, and a stray one
    /// would make every request past the edge a refusal while every direct one worked.</summary>
    [Fact]
    public void Reads_a_note_with_a_slash_after_it()
    {
        Assert.NotNull(LiveAsk.Read($"/live/{Token()}/", from: null, Secret, Now));
    }
}
