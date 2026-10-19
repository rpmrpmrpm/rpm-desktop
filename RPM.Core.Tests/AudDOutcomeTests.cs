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
}
