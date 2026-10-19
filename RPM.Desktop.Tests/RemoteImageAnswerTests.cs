using System;
using System.IO;
using System.Threading.Tasks;
using RPM.Desktop.Presentation.Common;
using Xunit;

namespace RPM.Desktop.Tests;

/// <summary>
/// What <see cref="RemoteImage.BytesAsync"/> answers, kept apart: a picture, "there is no picture here",
/// and "the picture could not be asked for". These must not share one null. Otherwise every caller has to
/// remember which it was. For example, a photo that TheAudioDB had deleted would stay in the artist
/// page's pager as a page that never showed.
/// </summary>
public class RemoteImageAnswerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rpm-answer").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(410)]
    [InlineData(403)]
    public void A_missing_picture_is_an_answer_of_no_picture(int code)
    {
        var attempt = RemoteImage.Unsuccessful(code, refusedByGate: false, ourOwnApi: false);

        Assert.True(attempt.Answer.Answers(out var bytes, out _));
        Assert.Null(bytes);
        Assert.False(attempt.AskAgain);
    }

    /// <summary>A 401 is this app failing to identify itself, never a host's opinion of a file. Answered as
    /// "no picture" it would be invisible. Station logos would then be refused for want of an app key,
    /// while the diagnostics said "nothing".</summary>
    [Theory]
    [InlineData(401, false)]
    [InlineData(401, true)]
    [InlineData(403, true)]
    public void Being_refused_is_not_an_answer_about_the_picture(int code, bool ourOwnApi)
    {
        var attempt = RemoteImage.Unsuccessful(code, refusedByGate: false, ourOwnApi);

        Assert.IsType<RPM.Fingerprint.Net.Asked<byte[]?>.Unanswered>(attempt.Answer);

        // Not at once: whatever is missing from the request will still be missing.
        Assert.False(attempt.AskAgain);
    }

    /// <summary>But a 403 from somebody else's host is that host refusing to be hotlinked, and it
    /// will refuse again. Asking for ever is worse than believing it.</summary>
    [Fact]
    public void A_stranger_refusing_to_be_hotlinked_is_an_answer()
    {
        var attempt = RemoteImage.Unsuccessful(403, refusedByGate: false, ourOwnApi: false);

        Assert.True(attempt.Answer.Answers(out var bytes, out _));
        Assert.Null(bytes);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(408)]
    [InlineData(429)]
    public void A_host_having_a_moment_is_not_an_answer_and_is_asked_again(int code)
    {
        var attempt = RemoteImage.Unsuccessful(code, refusedByGate: false, ourOwnApi: false);

        Assert.IsType<RPM.Fingerprint.Net.Asked<byte[]?>.Unanswered>(attempt.Answer);
        Assert.True(attempt.AskAgain);
    }

    /// <summary>Offline: not an answer about the picture, and not asked again at once, because the gate
    /// will refuse again.</summary>
    [Fact]
    public void The_app_being_offline_is_not_an_answer_and_is_not_asked_again_at_once()
    {
        var attempt = RemoteImage.Unsuccessful(503, refusedByGate: true, ourOwnApi: false);

        Assert.IsType<RPM.Fingerprint.Net.Asked<byte[]?>.Unanswered>(attempt.Answer);
        Assert.False(attempt.AskAgain);
    }

    [Fact]
    public async Task A_local_picture_is_answered_with_its_bytes()
    {
        var path = Path.Combine(_dir, "a.jpg");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);

        Assert.True((await RemoteImage.BytesAsync(new Uri(path).AbsoluteUri)).Answers(out var bytes, out _));
        Assert.Equal([1, 2, 3], bytes);
    }

    [Fact]
    public async Task A_local_file_that_is_not_there_is_an_answer_of_no_picture()
    {
        var path = Path.Combine(_dir, "gone.jpg");

        Assert.True((await RemoteImage.BytesAsync(new Uri(path).AbsoluteUri)).Answers(out var bytes, out _));
        Assert.Null(bytes);
    }

    /// <summary>A path that is there and cannot be read (a folder) is a failure, not "no
    /// picture".</summary>
    [Fact]
    public async Task A_local_file_that_cannot_be_read_is_not_an_answer()
        => Assert.IsType<RPM.Fingerprint.Net.Asked<byte[]?>.Unanswered>(
            await RemoteImage.BytesAsync(new Uri(_dir).AbsoluteUri));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not an address")]
    public async Task No_address_is_an_answer_of_no_picture(string? url)
    {
        Assert.True((await RemoteImage.BytesAsync(url)).Answers(out var bytes, out _));
        Assert.Null(bytes);
    }
}
