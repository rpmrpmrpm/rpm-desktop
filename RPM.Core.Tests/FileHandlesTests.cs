using RPM.Core.Files;

namespace RPM.Core.Tests;

public class FileHandlesTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    public FileHandlesTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"rpmhandles-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "track.flac");
        File.WriteAllBytes(_file, [1, 2, 3]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void IsWritable_is_true_for_a_file_nobody_holds()
    {
        Assert.True(FileHandles.IsWritable(_file));
    }

    [Fact]
    public void IsWritable_is_false_while_a_reader_holds_it()
    {
        using var held = File.Open(_file, FileMode.Open, FileAccess.Read, FileShare.Read);

        // What a player's decoder looks like from outside: open, shared for reading, not for writing.
        Assert.False(FileHandles.IsWritable(_file));
    }

    [Fact]
    public void IsWritable_is_true_for_a_path_that_does_not_exist()
    {
        // Nothing can be holding it, so a caller about to create it is not blocked.
        Assert.True(FileHandles.IsWritable(Path.Combine(_dir, "gone.flac")));
    }

    [Fact]
    public async Task WaitUntilWritableAsync_returns_once_the_handle_closes()
    {
        var held = File.Open(_file, FileMode.Open, FileAccess.Read, FileShare.Read);
        var releasing = Task.Run(async () =>
        {
            await Task.Delay(120);
            held.Dispose();
        });

        Assert.True(await FileHandles.WaitUntilWritableAsync(_file, TimeSpan.FromSeconds(5)));
        await releasing;
    }

    [Fact]
    public async Task WaitUntilWritableAsync_gives_up_on_a_handle_that_never_closes()
    {
        using var held = File.Open(_file, FileMode.Open, FileAccess.Read, FileShare.Read);

        // False tells the caller to report the file as refused, not to write into it anyway.
        Assert.False(await FileHandles.WaitUntilWritableAsync(_file, TimeSpan.FromMilliseconds(150)));
    }

    [Fact]
    public async Task WaitUntilWritableAsync_over_many_names_the_ones_still_held()
    {
        var free = Path.Combine(_dir, "free.flac");
        File.WriteAllBytes(free, [4]);
        using var held = File.Open(_file, FileMode.Open, FileAccess.Read, FileShare.Read);

        var stillHeld = await FileHandles.WaitUntilWritableAsync(
            [free, _file], TimeSpan.FromMilliseconds(150));

        Assert.Equal([_file], stillHeld);
    }
}
