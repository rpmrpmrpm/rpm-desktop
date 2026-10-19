using RPM.Core.Files;

namespace RPM.Core.Tests;

/// <summary>
/// A store's file is only ever replaced when there is nothing in it to keep. See
/// <see cref="StoreFile"/>.
/// </summary>
public class StoreFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rpm-store").FullName;

    private string StorePath => Path.Combine(_dir, "store.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static int Parse(string text) => text == "bad" ? throw new FormatException() : int.Parse(text);

    [Fact]
    public void No_file_is_nothing_to_read_and_may_be_written()
    {
        var read = StoreFile.Load<int?>(StorePath, t => Parse(t));

        Assert.Null(read.Value);
        Assert.True(read.MayWrite);
    }

    [Fact]
    public void A_file_is_read()
    {
        File.WriteAllText(StorePath, "42");

        var read = StoreFile.Load<int?>(StorePath, t => Parse(t));

        Assert.Equal(42, read.Value);
        Assert.True(read.MayWrite);
    }

    [Fact]
    public void A_file_that_does_not_parse_is_moved_aside_and_the_store_may_be_written()
    {
        File.WriteAllText(StorePath, "bad");

        var read = StoreFile.Load<int?>(StorePath, t => Parse(t));

        Assert.Null(read.Value);
        Assert.True(read.MayWrite);
        Assert.False(File.Exists(StorePath));
        Assert.Equal("bad", File.ReadAllText(StorePath + ".unreadable"));
    }

    [Fact]
    public void A_file_that_cannot_be_read_now_must_not_be_written_over()
    {
        if (OperatingSystem.IsWindows())
            return;

        File.WriteAllText(StorePath, "42");
        File.SetUnixFileMode(StorePath, UnixFileMode.None);
        try
        {
            var read = StoreFile.Load<int?>(StorePath, t => Parse(t));

            Assert.Null(read.Value);
            Assert.False(read.MayWrite);
        }
        finally
        {
            File.SetUnixFileMode(StorePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        Assert.Equal("42", File.ReadAllText(StorePath));
    }
}
