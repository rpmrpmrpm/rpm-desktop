using RPM.Core.Ripping;

namespace RPM.Core.Tests;

/// <summary>
/// The failure this exists to make impossible: a CSS disc that scrambles only <em>some</em> of its
/// packs is read as if it were clear. The rip finishes with exit code 0, the right duration and
/// full-scale white noise mixed through the music. CSS leaves the headers readable and only the
/// payload is gibberish, so nothing downstream has anything to complain about.
///
/// <para>
/// This happens on real discs: on one disc roughly a quarter of the packs are scrambled. A
/// detector that read only one pack would find it clear and call the disc unprotected.
/// </para>
/// </summary>
public class VobScrambleGuardTests : IDisposable
{
    private const int SectorSize = 2048;
    private readonly string _dir;

    public VobScrambleGuardTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"vobguard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static byte[] Sector(byte streamId, int scrambling)
    {
        var s = new byte[SectorSize];
        s[0] = 0x00; s[1] = 0x00; s[2] = 0x01; s[3] = 0xBA;
        s[13] = 0xF8;                                        // no stuffing
        s[14] = 0x00; s[15] = 0x00; s[16] = 0x01; s[17] = streamId;
        s[18] = 0x07; s[19] = 0xEC;
        s[20] = (byte)(0x80 | ((scrambling & 0x03) << 4));
        return s;
    }

    /// <summary>A VOB where every <paramref name="scrambleEvery"/>-th media pack is scrambled.</summary>
    private string BuildVob(int sectors, int scrambleEvery)
    {
        var path = Path.Combine(_dir, "VTS_01_1.VOB");
        using var file = File.Create(path);

        for (var i = 0; i < sectors; i++)
        {
            // Every VOBU opens with a NAV pack, which is never scrambled. A naive detector reads it and
            // mistakes it for evidence that the disc is clear.
            if (i % 16 == 0)
            {
                file.Write(Sector(0xBF, 0));
                continue;
            }

            var scrambled = scrambleEvery > 0 && i % scrambleEvery == 0;
            file.Write(Sector(0xBD, scrambled ? 1 : 0));
        }

        return path;
    }

    private static async Task<Exception?> CopyAsync(string vob, int sectors)
    {
        await using var vobs = new VobSectorStream([vob]);
        await using var sink = new MemoryStream();
        try
        {
            await vobs.CopySectorsAsync([new DvdCellRange(0, sectors - 1)], sink);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    [Fact]
    public async Task A_clear_disc_rips()
    {
        var vob = BuildVob(sectors: 256, scrambleEvery: 0);

        Assert.Null(await CopyAsync(vob, 256));
    }

    [Fact]
    public async Task A_fully_scrambled_disc_is_refused()
    {
        var vob = BuildVob(sectors: 256, scrambleEvery: 1);

        var ex = await CopyAsync(vob, 256);
        Assert.IsType<DvdEncryptedException>(ex);
        Assert.Contains("libdvdcss", ex!.Message);
    }

    [Fact]
    public async Task A_disc_that_scrambles_only_some_packs_is_refused_too()
    {
        // The case a naive detector gets wrong. Only a quarter of the packs are scrambled, so most of
        // the rip decodes to real music and the rest to white noise, with nothing reporting a fault.
        var vob = BuildVob(sectors: 4096, scrambleEvery: 4);

        var ex = await CopyAsync(vob, 4096);

        Assert.IsType<DvdEncryptedException>(ex);
    }

    [Fact]
    public async Task Even_one_scrambled_pack_in_a_whole_VOB_stops_the_rip()
    {
        // Nothing about a mostly-clear disc makes the scrambled part safe to ship: those seconds would
        // be noise. One is enough to refuse.
        var path = Path.Combine(_dir, "VTS_01_1.VOB");
        using (var file = File.Create(path))
        {
            for (var i = 0; i < 2000; i++)
                file.Write(Sector(0xBD, i == 1500 ? 1 : 0));
        }

        Assert.IsType<DvdEncryptedException>(await CopyAsync(path, 2000));
    }
}
