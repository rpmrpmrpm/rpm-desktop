using System.Net;
using System.Text;
using RPM.Core.Identify;
using RPM.Core.Models;
using RPM.Fingerprint.Chroma;

namespace RPM.Core.Tests;

/// <summary>
/// Whether a track went to AudD is a fact the clip endpoint's answer carries, and the Recognise
/// report counts it. The report must not read the account's standing instead of what happened: that
/// would say tracks "were sent to AudD" when AcoustID named all five and nothing was sent.
/// What is checked: each of the endpoint's three answers is read as what it is (the rpm database on
/// the server's second look is not AudD), and the free ladder never reports AudD at all.
/// </summary>
public class AudDOutcomeTests
{
    /// <summary>Answers every request with one fixed status and body.</summary>
    private sealed class Answer(HttpStatusCode status, string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
    }

    private static LambdaIdentificationBackend Server(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(new HttpClient(new Answer(status, json)) { BaseAddress = new Uri("https://identify.test/") },
            _ => Task.FromResult<string?>("token"));

    private static readonly FingerprintQuery Query = new("ZmFrZQ==", 200, [1, 2, 3]);

    private const string Track =
        "{\"title\":\"Padam Padam\",\"artist\":\"Kylie Minogue\",\"album\":\"Tension\",\"year\":2023,\"score\":0.95}";

    [Fact]
    public async Task An_AudD_match_is_a_track_AudD_identified()
    {
        var answer = await Server($"{{\"status\":\"matched\",\"source\":\"audd\",\"track\":{Track}}}")
            .IdentifyClipAsync(Query, [0]);

        Assert.IsType<ClipAnswer.AudDIdentified>(answer);
        Assert.Equal(AudDOutcome.Identified, answer.AudD);
        Assert.Equal("Padam Padam", answer.Match?.Title);
    }

    [Fact]
    public async Task An_unknown_answer_is_a_track_AudD_did_not_know()
    {
        var answer = await Server("{\"status\":\"unknown\",\"source\":\"audd\",\"track\":null}")
            .IdentifyClipAsync(Query, [0]);

        Assert.IsType<ClipAnswer.AudDNotIdentified>(answer);
        Assert.Equal(AudDOutcome.NotIdentified, answer.AudD);
        Assert.Null(answer.Match);
    }

    [Fact]
    public async Task A_match_from_the_rpm_database_on_the_second_look_did_not_reach_AudD()
    {
        var answer = await Server($"{{\"status\":\"matched\",\"source\":\"own-db\",\"track\":{Track}}}")
            .IdentifyClipAsync(Query, [0]);

        Assert.IsType<ClipAnswer.FromOwnDb>(answer);
        Assert.Equal(AudDOutcome.NotAsked, answer.AudD);
        Assert.Equal("Padam Padam", answer.Match?.Title);
    }

    [Fact]
    public async Task A_refusal_is_not_an_answer_at_all()
    {
        await Assert.ThrowsAsync<LambdaIdentificationBackend.SupportersOnlyException>(() =>
            Server("{\"error\":\"supporters only\"}", HttpStatusCode.Forbidden).IdentifyClipAsync(Query, [0]));
    }

    // ── what the identifier hands on ──

    private static TrackIdentifier Identifier(IIdentificationBackend backend) => new(backend)
    {
        Fingerprinter = _ => new FpcalcRunner.CompressedResult(200, "ZmFrZQ=="),
        RawFingerprinter = _ => [1, 2, 3],
        ClipCutter = (_, _) => [0],
    };

    private static readonly TrackInfo File = new() { FilePath = "D:/New music/track02.mp3", Extension = ".mp3" };

    [Fact]
    public async Task The_free_ladder_never_reports_AudD()
    {
        var named = await Identifier(Server($"{{\"status\":\"matched\",\"source\":\"acoustid\",\"track\":{Track}}}"))
            .IdentifyDetailedAsync(File);
        var missed = await Identifier(Server("{\"status\":\"unknown\",\"source\":null,\"track\":null}"))
            .IdentifyDetailedAsync(File);

        Assert.True(named.Matched);
        Assert.Equal(AudDOutcome.NotAsked, named.AudD);
        Assert.False(missed.Matched);
        Assert.Equal(AudDOutcome.NotAsked, missed.AudD);
    }

    [Fact]
    public async Task The_advanced_layer_hands_on_what_AudD_did()
    {
        var identified = await Identifier(Server($"{{\"status\":\"matched\",\"source\":\"audd\",\"track\":{Track}}}"))
            .IdentifyPremiumDetailedAsync(File);
        var notIdentified = await Identifier(Server("{\"status\":\"unknown\",\"source\":\"audd\",\"track\":null}"))
            .IdentifyPremiumDetailedAsync(File);
        var ownDb = await Identifier(Server($"{{\"status\":\"matched\",\"source\":\"own-db\",\"track\":{Track}}}"))
            .IdentifyPremiumDetailedAsync(File);

        Assert.Equal((true, AudDOutcome.Identified), (identified.Matched, identified.AudD));
        Assert.Equal((false, AudDOutcome.NotIdentified), (notIdentified.Matched, notIdentified.AudD));
        Assert.Equal((true, AudDOutcome.NotAsked), (ownDb.Matched, ownDb.AudD));
    }

    // ── one track up the ladder ──

    private const string Unknown = "{\"status\":\"unknown\",\"source\":null,\"track\":null}";

    /// <summary>The fingerprint lookup and the clip lookup, answered apart, with a count of the clips.</summary>
    private sealed class Ladder(HttpResponseMessage identify, Func<HttpResponseMessage> clip) : HttpMessageHandler
    {
        public int Clips { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("identify-clip", StringComparison.Ordinal))
                return Task.FromResult(identify);
            Clips++;
            return Task.FromResult(clip());
        }
    }

    private static HttpResponseMessage Said(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static TrackIdentifier Climbing(Ladder ladder) =>
        Identifier(new LambdaIdentificationBackend(
            new HttpClient(ladder) { BaseAddress = new Uri("https://identify.test/") },
            _ => Task.FromResult<string?>("token")));

    private static HttpResponseMessage Named =>
        Said(HttpStatusCode.OK, $"{{\"status\":\"matched\",\"source\":\"acoustid\",\"track\":{Track}}}");

    [Fact]
    public async Task A_track_the_basic_layers_name_never_reaches_the_advanced_layer()
    {
        var ladder = new Ladder(Named, () => throw new InvalidOperationException("the clip was sent"));

        var (outcome, lost) = await Climbing(ladder).ClimbAsync(File, advanced: true);

        Assert.True(outcome.Matched);
        Assert.Equal(TrackIdentifier.LostLayer.None, lost);
        Assert.Equal(0, ladder.Clips);
    }

    [Fact]
    public async Task An_account_without_the_advanced_layer_sends_no_clip()
    {
        var ladder = new Ladder(Said(HttpStatusCode.OK, Unknown), () => throw new InvalidOperationException("the clip was sent"));

        var (outcome, lost) = await Climbing(ladder).ClimbAsync(File, advanced: false);

        Assert.False(outcome.Matched);
        Assert.Equal(TrackIdentifier.LostLayer.None, lost);
        Assert.Equal(0, ladder.Clips);
    }

    [Fact]
    public async Task What_the_basic_layers_miss_goes_to_the_advanced_layer()
    {
        var ladder = new Ladder(Said(HttpStatusCode.OK, Unknown),
            () => Said(HttpStatusCode.OK, $"{{\"status\":\"matched\",\"source\":\"audd\",\"track\":{Track}}}"));

        var (outcome, lost) = await Climbing(ladder).ClimbAsync(File, advanced: true);

        Assert.True(outcome.Matched);
        Assert.Equal(AudDOutcome.Identified, outcome.AudD);
        Assert.Equal(TrackIdentifier.LostLayer.None, lost);
    }

    [Fact]
    public async Task Being_refused_the_advanced_layer_is_not_a_failure_of_the_track()
    {
        var ladder = new Ladder(Said(HttpStatusCode.OK, Unknown), () => Said(HttpStatusCode.Forbidden, "{}"));

        var (outcome, lost) = await Climbing(ladder).ClimbAsync(File, advanced: true);

        Assert.False(outcome.Matched);
        Assert.Equal(TrackIdentifier.LostLayer.Refused, lost);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task The_advanced_layer_being_down_leaves_the_run_on_the_basic_layers(HttpStatusCode status)
    {
        var ladder = new Ladder(Said(HttpStatusCode.OK, Unknown), () => Said(status, "{\"error\":\"premium recognition unavailable\"}"));

        var (outcome, lost) = await Climbing(ladder).ClimbAsync(File, advanced: true);

        Assert.False(outcome.Matched);
        Assert.Equal(TrackIdentifier.LostLayer.Down, lost);
    }

    [Fact]
    public async Task The_basic_layers_being_down_still_stops_the_run()
    {
        var ladder = new Ladder(Said(HttpStatusCode.ServiceUnavailable, "{}"), () => throw new InvalidOperationException("the clip was sent"));

        await Assert.ThrowsAsync<LambdaIdentificationBackend.IdentificationUnavailableException>(() =>
            Climbing(ladder).ClimbAsync(File, advanced: true));
    }
}
