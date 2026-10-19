using RPM.Core.Radio;

namespace RPM.Core.Tests;

/// <summary>The saved stations' merge rule, which the API, the Android apps and the iPhone hold too.</summary>
public class FavoriteLedgerTests
{
    private static RadioStation Station(string id) => new() { Uuid = id, Name = $"Radio {id}", StreamUrl = $"https://s/{id}" };

    private static FavoriteRow Row(string id, bool saved, long at, long stamp = 1) => new(id, saved, at, Station(id), stamp);

    [Fact]
    public void A_first_ledger_keeps_the_list_in_its_order_and_sends_all_of_it()
    {
        var ledger = FavoriteLedger.Of([Station("b"), Station("a"), Station("c")]);
        Assert.Equal(["b", "a", "c"], ledger.Saved.Select(s => s.Uuid));
        Assert.Equal(["b", "a", "c"], ledger.Unsent);
    }

    [Fact]
    public void A_save_goes_to_the_end_and_an_unsave_takes_it_out()
    {
        var ledger = FavoriteLedger.Of([Station("a"), Station("b")])
            .Changed(Station("c"), saved: true, now: 1000)
            .Changed(Station("a"), saved: false, now: 1001);
        Assert.Equal(["b", "c"], ledger.Saved.Select(s => s.Uuid));
        Assert.False(ledger.Holds("a"));
        Assert.True(ledger.Holds("C"));
    }

    [Fact]
    public void A_change_is_newer_than_the_one_it_replaces_whatever_the_clock_says()
    {
        var ledger = FavoriteLedger.Empty.Changed(Station("a"), true, 5000).Changed(Station("a"), false, 10);
        Assert.Equal(5001, ledger.Changes["a"].ChangedAt);
    }

    [Fact]
    public void A_newer_row_from_the_cloud_replaces_what_is_held_and_is_no_longer_ours_to_send()
    {
        var ledger = FavoriteLedger.Of([Station("a")]).Taking([Row("a", saved: false, at: 50, stamp: 7)]);
        Assert.False(ledger.Holds("a"));
        Assert.Empty(ledger.Unsent);
        Assert.Equal(7, ledger.Cursor);
    }

    [Fact]
    public void An_older_row_from_the_cloud_does_not_undo_a_change_made_here()
    {
        var ledger = FavoriteLedger.Empty.Changed(Station("a"), false, 900).Taking([Row("a", saved: true, at: 100)]);
        Assert.False(ledger.Holds("a"));
        Assert.Equal(["a"], ledger.Unsent);
    }

    [Fact]
    public void Another_devices_station_is_added_in_the_order_it_was_saved()
    {
        var ledger = FavoriteLedger.Empty.Changed(Station("mine"), true, 500)
            .Taking([Row("theirs", saved: true, at: 300), Row("later", saved: true, at: 800)]);
        Assert.Equal(["theirs", "mine", "later"], ledger.Saved.Select(s => s.Uuid));
    }

    [Fact]
    public void A_change_made_while_one_was_out_stays_unsent()
    {
        var before = FavoriteLedger.Empty.Changed(Station("a"), true, 100);
        var sent = before.ToSend(500);
        Assert.Equal(["a"], before.Changed(Station("a"), false, 200).Sent(sent).Unsent);
        Assert.Empty(before.Sent(sent).Unsent);
    }

    [Fact]
    public void Another_account_starts_an_empty_ledger_and_the_same_one_keeps_it()
    {
        var mine = FavoriteLedger.Of([Station("a")]).ForAccount("me");
        Assert.Equal(["a"], mine.ForAccount("me").Saved.Select(s => s.Uuid));
        var theirs = mine.ForAccount("someone else");
        Assert.Empty(theirs.Saved);
        Assert.Empty(theirs.Unsent);
        Assert.Equal(0, theirs.Cursor);
    }

    [Fact]
    public void The_cursor_never_goes_back()
    {
        var ledger = (FavoriteLedger.Empty with { Cursor = 10 }).Taking([Row("a", saved: true, at: 1, stamp: 4)]);
        Assert.Equal(10, ledger.Cursor);
    }

    [Fact]
    public void A_refresh_keeps_the_stations_place_and_sends_nothing()
    {
        var ledger = FavoriteLedger.Of([Station("a"), Station("b")]).Sent(FavoriteLedger.Of([Station("a"), Station("b")]).ToSend(500));
        var renamed = Station("a") with { Name = "New name" };
        var refreshed = ledger.Refreshed(renamed);
        Assert.Equal(["a", "b"], refreshed.Saved.Select(s => s.Uuid));
        Assert.Equal("New name", refreshed.Saved[0].Name);
        Assert.Empty(refreshed.Unsent);
    }
}
