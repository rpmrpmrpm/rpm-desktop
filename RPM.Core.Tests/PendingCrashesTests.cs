using System.Text.Json;
using RPM.Core.Crashes;

namespace RPM.Core.Tests;

public class PendingCrashesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pending-{Guid.NewGuid():N}");

    public PendingCrashesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private void Put(string id) => File.WriteAllText(Path.Combine(_dir, $"{id}.json"), JsonSerializer.Serialize(
        new PendingCrash(id, "mac", "0.3.1", "macOS", "arm64", 1, "boom", null, null, null), CrashJson.Default.PendingCrash));

    [Fact]
    public void Reports_are_read_and_removed_by_id()
    {
        Put("crash_000000000000000000000001");
        Put("crash_000000000000000000000002");
        var store = new PendingCrashes(_dir);

        Assert.Equal(2, store.Read().Count);
        store.Remove("crash_000000000000000000000001");
        Assert.Equal(["crash_000000000000000000000002"], store.Read().Select(p => p.CrashId));
        store.RemoveAll();
        Assert.Empty(store.Read());
    }

    [Fact]
    public void An_unreadable_report_is_removed_rather_than_offered_forever()
    {
        File.WriteAllText(Path.Combine(_dir, "crash_bad.json"), "{");

        Assert.Empty(new PendingCrashes(_dir).Read());
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void A_missing_folder_is_no_reports()
    {
        Assert.Empty(new PendingCrashes(Path.Combine(_dir, "absent")).Read());
    }
}
