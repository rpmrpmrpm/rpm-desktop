using System;
using System.IO;
using System.Threading.Tasks;
using RPM.Desktop.Presentation.Common;
using SkiaSharp;
using Xunit;

namespace RPM.Desktop.Tests;

/// <summary>
/// Measuring a photograph without fetching it.
///
/// <para>The app saves the most bandwidth on this assumption. Choosing which photograph an artist wears
/// means comparing the shapes of several of them. Downloading each one in full, just to read a few bytes
/// at the front, would waste bandwidth. An artist has four press shots, a genre card has a couple of
/// artists, and there are thirty-six cards. This test checks that the front of a picture is enough to say
/// how big it is. A kilobyte from a ranged request is all <see cref="RemoteImage.BoundsAsync"/> gives the
/// reader to work with.</para>
/// </summary>
public class RemoteImageBoundsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rpm-bounds").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // A temp directory that will not go is the operating system's problem, not the test's.
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>A picture big enough that reading it whole and reading its front are plainly different
    /// amounts of work.</summary>
    private static byte[] Picture(int width, int height)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        surface.Canvas.Clear(SKColors.DarkSlateGray);

        // Noise, so the encoder cannot flatten the whole thing into a few hundred bytes and make the
        // truncation below meaningless.
        var paint = new SKPaint();
        var roll = new Random(4);
        for (var i = 0; i < 4000; i++)
        {
            paint.Color = new SKColor(
                (byte)roll.Next(256), (byte)roll.Next(256), (byte)roll.Next(256));
            surface.Canvas.DrawRect(
                roll.Next(width), roll.Next(height), roll.Next(40), roll.Next(40), paint);
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 92);
        return data.ToArray();
    }

    private string Wrote(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string Address(string path) => new Uri(path).AbsoluteUri;

    /// <summary>A photograph on this disk is measured to what it actually is. This is the path a saved
    /// backdrop takes: the person chose the picture, so it travels as a file:// address.</summary>
    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(600, 900)]
    [InlineData(1000, 1000)]
    public async Task A_picture_on_disk_is_measured(int width, int height)
    {
        var path = Wrote($"{width}x{height}.png", Picture(width, height));

        Assert.Equal((width, height), await RemoteImage.BoundsAsync(Address(path)));
    }

    /// <summary>
    /// THE ONE THAT MATTERS: the front of a picture says how big the whole picture is.
    ///
    /// <para>A ranged request brings back a kilobyte and no more, so if the size could only be read off
    /// the complete file then measuring by header would quietly answer nothing for every candidate and
    /// the choice of photograph would fall back to a guess on every artist in the collection. Here the
    /// picture is cut down to what such a request would return, and it still measures.</para>
    /// </summary>
    [Fact]
    public async Task A_kilobyte_off_the_front_is_enough_to_measure_by()
    {
        var whole = Picture(1920, 1080);
        Assert.True(whole.Length > 8 * 1024, "the picture has to be bigger than the front of it");

        var front = whole[..1024];
        var path = Wrote("front.png", front);

        Assert.Equal((1920, 1080), await RemoteImage.BoundsAsync(Address(path)));
    }

    /// <summary>Nothing measurable is zero by zero and never a throw: the caller's next move is the next
    /// candidate photograph, and it has that move for a picture that will not read as well as for one
    /// that is not there.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not an address at all")]
    [InlineData("/a/plain/path/with/no/scheme.png")]
    public async Task An_address_that_is_not_a_picture_measures_nothing(string url)
        => Assert.Equal((0, 0), await RemoteImage.BoundsAsync(url));

    [Fact]
    public async Task A_file_that_is_not_there_measures_nothing()
        => Assert.Equal((0, 0), await RemoteImage.BoundsAsync(
            Address(Path.Combine(_dir, "nobody-wrote-this.png"))));

    [Fact]
    public async Task A_file_that_is_not_a_picture_measures_nothing()
        => Assert.Equal((0, 0), await RemoteImage.BoundsAsync(
            Address(Wrote("notes.txt", "this is not a photograph"u8.ToArray()))));

    /// <summary>A caller that has moved on is told so, rather than handed a pair of zeroes. The two are
    /// different facts, and the choosing walk acts on the difference. An unmeasurable picture is passed
    /// over and the walk goes on. A walk that was cut short must settle nothing at all, because a
    /// photograph is chosen for an artist ONCE per session. A choice made on half a walk is the one they
    /// would be stuck with.</summary>
    [Fact]
    public async Task A_walk_that_was_cut_short_is_not_a_picture_that_would_not_measure()
    {
        var path = Wrote("cancelled.png", Picture(1920, 1080));
        using var gone = new System.Threading.CancellationTokenSource();
        gone.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RemoteImage.BoundsAsync(Address(path), gone.Token));
    }
}
