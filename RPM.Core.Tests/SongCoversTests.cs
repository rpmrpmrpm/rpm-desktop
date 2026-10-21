using RPM.Core.Net;
using RPM.Core.Radio;
using RPM.Fingerprint.Catalog;

namespace RPM.Core.Tests;

public class SongCoversTests
{
    private const string Cover = "https://img.example/halo/600x600bb.jpg";

    /// <summary>An ask that counts how often it was made, and answers with what it was told to.</summary>
    private sealed class Ask(Func<Asked<CatalogSongCover>> answer)
    {
        public int Made;

        public Task<Asked<CatalogSongCover>> Run(string artist, string title, CancellationToken ct)
        {
            Interlocked.Increment(ref Made);
            return Task.FromResult(answer());
        }
    }

    private static Asked<CatalogSongCover> Found(string? cover = Cover)
        => Asked.Answer(new CatalogSongCover(cover, "I Am... Sasha Fierce", "Beyoncé", "Halo"));

    [Fact]
    public void Two_spellings_of_one_song_share_a_key()
    {
        Assert.Equal(SongCovers.KeyOf("Beyoncé", "Halo"), SongCovers.KeyOf("  BEYONCE ", "halo"));
        Assert.Equal(SongCovers.KeyOf("Daft Punk", "One  More Time"), SongCovers.KeyOf("daft punk", "One More Time"));
    }

    [Fact]
    public void The_halves_are_kept_apart()
    {
        Assert.NotEqual(SongCovers.KeyOf("A B", "C"), SongCovers.KeyOf("A", "B C"));
    }

    [Fact]
    public async Task A_found_cover_is_asked_for_once()
    {
        var ask = new Ask(() => Found());
        var covers = new SongCovers(ask.Run);

        Assert.Equal(Cover, await covers.CoverAsync("Beyoncé", "Halo"));
        Assert.Equal(Cover, await covers.CoverAsync("BEYONCE", "halo"));
        Assert.Equal(1, ask.Made);
    }

    [Fact]
    public async Task A_song_with_no_cover_is_kept_as_having_none()
    {
        var ask = new Ask(() => Asked.Answer(CatalogSongCover.None));
        var covers = new SongCovers(ask.Run);

        Assert.Null(await covers.CoverAsync("Nobody", "Holds This"));
        Assert.Null(await covers.CoverAsync("Nobody", "Holds This"));
        Assert.Equal(1, ask.Made);
    }

    [Fact]
    public async Task A_failed_ask_is_forgotten_and_asked_again()
    {
        var fails = true;
        var ask = new Ask(() => fails ? Asked.Failed<CatalogSongCover>("HTTP 503") : Found());
        var covers = new SongCovers(ask.Run);

        Assert.Null(await covers.CoverAsync("Beyoncé", "Halo"));
        fails = false;
        Assert.Equal(Cover, await covers.CoverAsync("Beyoncé", "Halo"));
        Assert.Equal(2, ask.Made);
    }

    [Fact]
    public async Task An_ask_that_throws_is_a_failure_not_an_error()
    {
        var covers = new SongCovers((_, _, _) => throw new HttpRequestException("offline"));

        Assert.Null(await covers.CoverAsync("Beyoncé", "Halo"));
    }

    /// <summary>An advert, a jingle or the station's own name reaches SongCovers with no artist, and a
    /// half like "-" is not a name. None of these is asked about.</summary>
    [Theory]
    [InlineData(null, "Radio 538")]
    [InlineData("", "Halo")]
    [InlineData("Beyoncé", null)]
    [InlineData("-", "Halo")]
    [InlineData("Beyoncé", " X ")]
    public async Task What_is_not_a_song_is_not_asked_about(string? artist, string? title)
    {
        var ask = new Ask(() => Found());
        var covers = new SongCovers(ask.Run);

        Assert.Null(await covers.CoverAsync(artist, title));
        Assert.Equal(0, ask.Made);
    }

    [Fact]
    public async Task Only_an_https_address_is_a_cover()
    {
        var covers = new SongCovers(new Ask(() => Found("http://img.example/halo.jpg")).Run);

        Assert.Null(await covers.CoverAsync("Beyoncé", "Halo"));
    }

    /// <summary>The header moves on when the station announces the next song. The wait is cancelled, but the
    /// ask runs to the end, so the answer is there the next time the song plays.</summary>
    [Fact]
    public async Task A_cancelled_wait_keeps_the_answer()
    {
        var answer = new TaskCompletionSource<Asked<CatalogSongCover>>();
        var made = 0;
        var covers = new SongCovers((_, _, _) =>
        {
            made++;
            return answer.Task;
        });

        using var leave = new CancellationTokenSource();
        var waiting = covers.CoverAsync("Beyoncé", "Halo", leave.Token);
        leave.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        answer.SetResult(Found());
        Assert.Equal(Cover, await covers.CoverAsync("Beyoncé", "Halo"));
        Assert.Equal(1, made);
    }
}
