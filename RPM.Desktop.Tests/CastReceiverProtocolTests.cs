using System.Text.RegularExpressions;
using RPM.Core.Audio;
using RPM.Desktop.Services.Cast;

namespace RPM.Desktop.Tests;

/// <summary>
/// The two ends of the Cast control channel, held to the same protocol.
///
/// <para>One side is a C# class, and the other is a web page in the rpm repo. Only
/// these tests connect them. If a message type is renamed on one side, the code still compiles, deploys and
/// runs, but the message never arrives. The failure is silent. The music plays and the television is wrong,
/// or a press on the speaker does nothing. It is only found by casting to a real device.</para>
///
/// <para>These tests check the vocabulary and the version, not the behaviour. Whether the page draws a good
/// screen needs a person to judge. Whether it listens for the words the app actually sends can be checked
/// by a test.</para>
/// </summary>
public class CastReceiverProtocolTests
{
    /// <summary>
    /// The receiver page, <c>cast/receiver.html</c> in the rpm repo, which must be checked out next to this
    /// repo. A checkout that holds every app in one tree has it in its own <c>cast</c> folder.
    /// </summary>
    private static string Page()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var sibling = Path.Combine(dir.FullName, "rpm", "cast", "receiver.html");
            if (File.Exists(sibling))
                return File.ReadAllText(sibling);

            var own = Path.Combine(dir.FullName, "cast", "receiver.html");
            if (File.Exists(own))
                return File.ReadAllText(own);
        }

        throw new FileNotFoundException(
            "cast/receiver.html was not found. It is in the rpm repo: check it out next to this repo.");
    }

    [Fact]
    public void The_Namespace_Is_The_Same_On_Both_Ends()
    {
        // The message namespace is one that no other app uses. Two spellings of it would make a channel
        // with only one end.
        Assert.Contains($"'{CastControlChannel.ControlNamespace}'", Page());
    }

    [Fact]
    public void The_Protocol_Version_Is_The_Same_On_Both_Ends()
    {
        // The receiver echoes this version back in its greeting. That lets us see when an old app talks to
        // a new receiver. The echo is only useful while both ends agree on the current number.
        var declared = Regex.Match(Page(), @"const PROTOCOL\s*=\s*(\d+)");
        Assert.True(declared.Success, "the receiver declares no PROTOCOL");
        Assert.Equal(CastControlChannel.Protocol.ToString(), declared.Groups[1].Value);
    }

    [Theory]
    // Every message the app sends to the television. The page's control listener needs a branch for each
    // one. A type with no branch is dropped without any error.
    [InlineData("meta")]     // the track changed under a stream that did not stop
    [InlineData("art")]      // the cover, as bytes, for a receiver that would not fetch the URL
    [InlineData("screen")]   // the picture off, or back on, with the music untouched
    [InlineData("favorite")] // whether this record is loved, so the heart can be drawn
    public void Everything_The_App_Sends_Is_Something_The_Receiver_Listens_For(string type)
    {
        Assert.Contains($"message.type === '{type}'", Page());
    }

    [Theory]
    // …and the other way. A message the page sends that the channel's switch has no case for crosses the
    // network and is thrown away.
    [InlineData("next")]      // next pressed at the speaker, or spoken at it
    [InlineData("prev")]
    [InlineData("hello")]     // what this device can decode, said on connect
    [InlineData("error")]     // what the player refused, and CAF's code for why
    [InlineData("playing")]   // sound actually started, and how long after the load
    [InlineData("starved")]   // it ran dry after playing: a hole in the music
    [InlineData("load")]
    [InlineData("artfail")]   // the cover would not load from the URL we gave it
    [InlineData("favorite")]  // the heart was pressed in the room
    public void Everything_The_Receiver_Says_Is_Something_The_App_Understands(string type)
    {
        // The page's own side. The page must still send these types, or the case here is dead code that
        // guards nothing.
        Assert.Contains($"type: '{type}'", Page());

        // …and the channel's side. This is the switch in OnMessageReceived, the only place where a received
        // message is acted on.
        var channel = File.ReadAllText(SourceOf("RPM.Desktop/Services/Cast/CastControlChannel.cs"));
        Assert.Contains($"case \"{type}\":", channel);
    }

    // ── what the device says it can do ─────────────────────────────────────────────────────────

    /// <summary>Lossless support is decided by the PLAYER's answer, not by the platform's. A Shield, a box
    /// that demuxes Matroska all day, must be told that it can play FLAC.
    ///
    /// <para><c>canDisplayType</c> answers from a list of MIME strings that the platform publishes, and
    /// <c>audio/x-matroska</c> is not on that list. The media element that holds the stream is the thing
    /// that owns the demuxer. Surround already works this way, and lossless must use the same split.</para>
    /// </summary>
    [Fact]
    public void Lossless_Believes_The_Player_Over_The_Platform()
    {
        var platformSaysNo = new CastCapabilities(
            Protocol: 1, Ac3: false, Eac3: false, Flac: false, HiResAudio: false,
            Display: true, Ac3Playable: false, FlacPlayable: true);

        Assert.True(CastDeviceProfile.From(Device("Shield"), platformSaysNo).Flac);
    }

    /// <summary>
    /// A yes from either spelling of the container is a yes.
    ///
    /// <para>The two names are the same demuxer, and a device may have registered only one of them. Folding
    /// the answers with <c>??</c> would stop at the first probe that answered. A first probe that answers
    /// "no" is exactly the case the second probe is for.</para>
    /// </summary>
    [Fact]
    public void A_Yes_From_Either_Spelling_Of_The_Container_Carries_It()
    {
        var answers = new Dictionary<string, string>
        {
            ["mka-flac"] = "no",
            ["mkv-flac"] = "probably",
        };

        Assert.True(CastControlChannel.WillPlayAny(answers, "mka-flac", "mkv-flac"));
    }

    /// <summary>If both answer no, the answer is no. If neither answers, there is no answer at all. A
    /// receiver too old to be asked must fall through to the platform, and must not be recorded as
    /// refusing.
    /// </summary>
    [Fact]
    public void A_Question_Nobody_Answered_Stays_Open()
    {
        var refused = new Dictionary<string, string> { ["mka-flac"] = "no", ["mkv-flac"] = "" };
        Assert.False(CastControlChannel.WillPlayAny(refused, "mka-flac", "mkv-flac"));

        Assert.Null(CastControlChannel.WillPlayAny(new Dictionary<string, string>(), "mka-flac", "mkv-flac"));
        Assert.Null(CastControlChannel.WillPlayAny(null, "mka-flac", "mkv-flac"));
    }

    /// <summary>The page must ask both of them. Otherwise the fold above has only one answer to decide
    /// from.</summary>
    [Theory]
    [InlineData("'mka-flac': 'audio/x-matroska; codecs=\"flac\"'")]
    [InlineData("'mkv-flac': 'video/x-matroska; codecs=\"flac\"'")]
    public void The_Receiver_Asks_Both_Spellings_Of_The_Container(string probe)
    {
        Assert.Contains(probe, Page());
    }

    /// <summary>
    /// A device that did not say whether it has a screen is not written down as having none.
    ///
    /// <para>The blackout switch is removed from the flyout for a speaker. "We were not told" does not mean
    /// a speaker. It may be a stock receiver, or a device whose capabilities call failed. If sent as false,
    /// either case would remove the switch from a television.</para>
    /// </summary>
    [Fact]
    public void A_Device_That_Did_Not_Say_Is_Not_Recorded_As_Screenless()
    {
        var unsaid = new CastCapabilities(
            Protocol: 1, Ac3: false, Eac3: false, Flac: false, HiResAudio: false,
            Display: null, Ac3Playable: false, FlacPlayable: false);

        var profile = CastDeviceProfile.From(Device("Speaker"), unsaid);

        Assert.Null(profile.Display);

        // …and the page leaves the field out rather than filling it in with a guess.
        Assert.Contains("typeof capabilities.display_supported === 'boolean'", Page());
    }

    /// <summary>
    /// A device says whether it has a picture by what it advertises, before anything connects to it. This
    /// matters. <b>A custom web receiver does not launch on an audio-only Cast device at all.</b> So the
    /// speakers, where the blackout switch means nothing, are exactly the devices that can never send a
    /// <c>hello</c> saying so. If the switch depended on the greeting alone, it would stay available on
    /// every speaker in the house.
    /// </summary>
    [Theory]
    [InlineData("Chromecast Audio", false, true)]
    [InlineData("Google Home Mini", false, true)]
    [InlineData("Google Nest Audio", false, true)]
    [InlineData("Google Cast Group", true, true)]
    // A hub is a speaker with a screen on the front. It says so before it says any of the words above,
    // which is why that word is checked first.
    [InlineData("Google Nest Hub Max", false, false)]
    [InlineData("Chromecast Ultra", false, false)]
    [InlineData("SHIELD Android TV", false, false)]
    // No rule covers this one. An unrecognised model keeps the switch, because leaving a setting available
    // is the safe direction.
    [InlineData("Some Television", false, false)]
    public void A_Speaker_Says_It_Has_No_Picture_By_What_It_Is(string model, bool group, bool screenless)
    {
        var device = new CastDevice("id", "Kitchen", model, System.Net.IPAddress.Loopback, group,
                                    new Sharpcaster.Models.ChromecastReceiver());

        Assert.Equal(screenless, device.HasNoScreen);
    }

    // ── the screen holds still ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The mark moves by <c>transform</c> and nothing else.
    ///
    /// <para>Do not pin the corner with <c>right</c>/<c>bottom</c> and the centre with
    /// <c>left</c>/<c>top</c>. Each state would then leave <c>auto</c> on the edges that the other state
    /// uses, and <c>auto</c> does not animate. The mark would jump to the middle of the screen, behind the
    /// sleeve, when a stream ends. This rule prevents that. A tidy-up can easily undo it.</para>
    /// </summary>
    [Fact]
    public void The_Mark_Travels_By_Transform_Alone()
    {
        var idle = Regex.Match(Page(), @"body\.idle #mark \{(?<body>[^}]*)\}");
        Assert.True(idle.Success, "the receiver has no rule for the mark while nothing is playing");

        foreach (var edge in new[] { "right", "bottom", "left", "top", "height" })
            Assert.DoesNotContain($"{edge}:", idle.Groups["body"].Value);
    }

    /// <summary>
    /// The end of a stream only proposes the mark. Anything that arrives after that takes the proposal
    /// back.
    ///
    /// <para>Next, previous, and stop-then-play all reach the receiver as the end of one stream and the
    /// start of another, with a gap in between. If the mark acted on that gap at once, the screen would
    /// empty. That is what a skip would look like from the sofa.</para>
    /// </summary>
    [Fact]
    public void A_Stream_Ending_Does_Not_Empty_The_Screen_At_Once()
    {
        var page = Page();

        Assert.Contains("idleTimer = window.setTimeout(goIdle, IDLE_AFTER_MS)", page);

        // Both signs that music is coming take the proposal back: the LOAD, which is the earliest sign, and
        // the record being painted.
        Assert.Equal(2, Regex.Matches(page, @"\bmusicIsComing\(\);").Count);
    }

    /// <summary>
    /// A new stream taking the old one's place does not propose the mark.
    ///
    /// <para>Every skip, every seek and every track started by hand is a cut followed by a LOAD. The LOAD
    /// reaches the receiver while the old media element still has buffered audio. CAF ends the old media
    /// and reports that as <c>MEDIA_FINISHED</c> with <c>INTERRUPTED</c>. That report comes AFTER the LOAD
    /// has already taken back every proposal. If the mark were armed on that report, nothing would be left
    /// to cancel the clock. Four seconds into a record that was playing, the mark would travel to the
    /// middle of the screen.</para>
    /// </summary>
    [Fact]
    public void A_New_Stream_Taking_Over_Is_Not_A_Stream_Ending()
    {
        var page = Page();

        Assert.Contains("EndedReason.INTERRUPTED", page);
        Assert.Contains("EndedReason.SKIPPED", page);
    }

    /// <summary>
    /// The mark is never shown over music that is playing.
    ///
    /// <para>The proposal is made about a stream that ended, and it is acted on four seconds later. Those
    /// are two different questions. This check is the one that holds whenever it is asked: is there sound
    /// in the room now?</para>
    /// </summary>
    [Fact]
    public void The_Screen_Is_Not_Emptied_While_The_Player_Is_Playing()
    {
        var page = Page();

        var idle = Regex.Match(page, @"function goIdle\(\)[\s\S]*?\n        \}");
        Assert.True(idle.Success, "the receiver has no goIdle");

        Assert.Contains("getPlayerState()", idle.Value);
        Assert.Contains("PlayerState.PLAYING", idle.Value);
        Assert.Contains("PlayerState.BUFFERING", idle.Value);
    }

    /// <summary>Each line is as tall as its own type, whatever is in it. The size must be set on the line
    /// itself. If it were set on a span inside, an empty artist or album would leave the line at the body's
    /// height. The column under the cover would then be shorter for that track, and the record it centres
    /// would sit in the wrong place.
    /// </summary>
    [Theory]
    [InlineData("#title-line")]
    [InlineData("#artist-line")]
    [InlineData("#album-line")]
    public void Every_Line_Carries_Its_Own_Type(string line)
    {
        Assert.Matches($@"{line}\s+\{{[^}}]*font-size:", Page());
    }

    /// <summary>For the tests here, a speaker is only its id and its name. Those are the only values read
    /// from it.</summary>
    private static CastDevice Device(string name) =>
        new("id", name, "", System.Net.IPAddress.Loopback, IsGroup: false, new Sharpcaster.Models.ChromecastReceiver());

    /// <summary>A file in the RPM tree, found the same way the page is.</summary>
    private static string SourceOf(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
                return path;
            dir = dir.Parent;
        }

        throw new FileNotFoundException($"{relative} was not found above {AppContext.BaseDirectory}");
    }
}
