using Microsoft.UI.Xaml;
using RPM.Core.Sharing;
using RPM.Desktop.Presentation.Main.Transport.Controls;

namespace RPM.Desktop.Tests;

/// <summary>
/// What the deck's share button offers when it is pressed: the collections this computer can be
/// standing in, which is the user's own and every friend's shelf that has actually been taken.
///
/// <para>The rule worth pinning down is the empty one. A list of nothing is what takes the button off the
/// deck entirely. So anything that wrongly counts as a shelf puts a button on the deck that opens onto
/// nothing. Anything wrongly left out hides the only way back to your own records.</para>
/// </summary>
public sealed class SharedLibraryRowTests
{
    private const string Own = "My library";
    private const string Here = "On this network";
    private const string Away = "Not on this network";

    private static TrustedPeer Peer(
        string id, string name, PeerKind kind = PeerKind.Guest, string face = "") =>
        new() { DeviceId = id, PublicKey = [1, 2, 3], Name = name, Face = face, Kind = kind };

    private static IReadOnlyList<SharedLibraryRow> Build(
        IEnumerable<TrustedPeer> peers,
        IEnumerable<string>? shelves = null,
        IEnumerable<string>? here = null,
        string? viewing = null,
        IReadOnlyDictionary<string, PeerFace>? faces = null,
        PeerFace? own = null,
        ShelfAccess access = ShelfAccess.Open)
    {
        var stored = new HashSet<string>(shelves ?? [], StringComparer.OrdinalIgnoreCase);
        var reachable = new HashSet<string>(here ?? [], StringComparer.OrdinalIgnoreCase);

        PeerFace Face(string id) =>
            faces is not null && faces.TryGetValue(id, out var face) ? face : PeerFace.Nobody;

        return SharedLibraryRow.Build(
            peers, access, stored.Contains, reachable.Contains, Face, viewing,
            own ?? PeerFace.Nobody, Own, Here, Away);
    }

    [Fact]
    public void Nobody_shared_is_no_list_at_all()
    {
        Assert.Empty(Build([]));
    }

    [Fact]
    public void A_friend_whose_shelf_has_never_been_taken_is_not_offered()
    {
        // Paired, but never seen on the network since pairing: opening it would draw an empty wall.
        Assert.Empty(Build([Peer("A", "Anna")]));
    }

    [Fact]
    public void Another_of_your_own_computers_is_not_a_library_to_visit()
    {
        // Its records are already on this shelf. There is nowhere to go.
        Assert.Empty(Build([Peer("A", "My PC", PeerKind.Account)], shelves: ["A"]));
    }

    // ── signed out ──

    [Fact]
    public void A_shelf_is_not_offered_to_somebody_signed_out()
    {
        // Opening one needs an account: half of a borrowed shelf is records the lender put in the
        // cloud, and the URL that plays one is issued to whoever is asking. So the switch goes,
        // rather than standing on the deck asking for a login every time it is pressed.
        Assert.Empty(Build([Peer("A", "Anna")], shelves: ["A"], access: ShelfAccess.NeedsAccount));
    }

    [Fact]
    public void Signing_out_on_a_friends_shelf_leaves_the_way_home()
    {
        // The shelves go, but the row that steps back into the user's own records stays: going home
        // is never gated, and somebody signed out on a borrowed wall with no exit thinks their own
        // collection has gone.
        var rows = Build(
            [Peer("A", "Anna")], shelves: ["A"], viewing: "A", access: ShelfAccess.NeedsAccount);

        Assert.Single(rows);
        Assert.True(rows[0].IsOwn);
        Assert.False(rows[0].IsOpen);
    }

    [Fact]
    public void Your_own_library_leads_the_list_and_is_the_one_you_are_in()
    {
        var rows = Build([Peer("A", "Anna")], shelves: ["A"]);

        Assert.Collection(rows,
            row =>
            {
                Assert.Equal("", row.DeviceId);
                Assert.Equal(Own, row.Name);
                Assert.True(row.IsOwn);
                Assert.True(row.IsOpen);
            },
            row =>
            {
                Assert.Equal("A", row.DeviceId);
                Assert.Equal("Anna", row.Name);
                Assert.False(row.IsOwn);
                Assert.False(row.IsOpen);
            });
    }

    [Fact]
    public void Standing_in_a_friends_shelf_moves_the_tick_to_them()
    {
        var rows = Build([Peer("A", "Anna")], shelves: ["A"], viewing: "A");

        Assert.False(rows[0].IsOpen);
        Assert.True(rows[1].IsOpen);
    }

    [Fact]
    public void A_device_id_cased_differently_is_the_same_computer()
    {
        var rows = Build([Peer("abc", "Anna")], shelves: ["ABC"], viewing: "AbC");

        Assert.True(rows[1].IsOpen);
    }

    [Fact]
    public void Whether_they_are_here_is_said_under_their_name()
    {
        var rows = Build(
            [Peer("A", "Anna"), Peer("B", "Bram")],
            shelves: ["A", "B"],
            here: ["A"]);

        Assert.Equal(Here, rows[1].Status);
        Assert.Equal(Away, rows[2].Status);
    }

    [Fact]
    public void Your_own_row_says_nothing_about_the_network()
    {
        var rows = Build([Peer("A", "Anna")], shelves: ["A"]);

        Assert.Equal("", rows[0].Status);
    }

    [Fact]
    public void The_way_out_survives_a_shelf_disappearing_under_you()
    {
        // Forgotten from the other screen while their wall was up. Nothing is left to offer, but the
        // one row that takes the user back to their own records has to stay on the button.
        var rows = Build([], viewing: "A");

        Assert.Single(rows);
        Assert.True(rows[0].IsOwn);
        Assert.False(rows[0].IsOpen);
    }

    [Fact]
    public void Your_own_row_is_named_after_the_collection_and_not_the_person_signed_into_it()
    {
        // The face on the own row is this account's; the NAME under it is still the collection's. A
        // friend's row is somebody to visit, and this one is where you already are.
        var rows = Build(
            [Peer("A", "GIOVANNI-PC-WIN")],
            shelves: ["A"],
            own: new PeerFace("Giovanni", new Uri("https://api.rpmrpmrpm.com/profiles/g/avatar")));

        Assert.Equal(Own, rows[0].Name);
    }

    // ── the tile on your own row ──

    [Fact]
    public void Every_other_row_takes_the_band()
    {
        // The list is drawn as one column of the data grid, and a grid bands alternate rows. Nothing
        // in the template knows what position a row is in, so the row is told here.
        var rows = Build([Peer("A", "Anna"), Peer("B", "Bo")], shelves: ["A", "B"]);

        Assert.Equal([false, true, false], rows.Select(r => r.IsBanded));
    }

    [Fact]
    public void Your_own_row_wears_the_picture_set_in_the_settings()
    {
        var rows = Build(
            [Peer("A", "Anna")],
            shelves: ["A"],
            own: new PeerFace("Giovanni", new Uri("https://api.rpmrpmrpm.com/profiles/g/avatar")));

        Assert.NotNull(rows[0].Avatar);
        Assert.Equal(Visibility.Visible, rows[0].PhotoVisibility);
        Assert.Equal(Visibility.Collapsed, rows[0].GlyphVisibility);
        Assert.Equal(Visibility.Collapsed, rows[0].InitialVisibility);

        // And it is the row on screen, which is what the selection wash and the bar down its left
        // are drawn from.
        Assert.True(rows[0].IsOpen);
        Assert.Equal(Visibility.Visible, rows[0].OpenVisibility);
    }

    /// <summary>
    /// The roomy look's lit tile: filled, unless there is a face to cover.
    /// </summary>
    /// <remarks>
    /// The two are exclusive by construction and that is the whole of what these hold. A fill over a
    /// photograph covers the one thing the tile is there to show, and a ring on an empty tile is a
    /// ring round nothing: so which of the pair is shown is the avatar's question, and whether either
    /// is shown at all is <c>IsOpen</c>'s.
    ///
    /// <para>Both looks are part of the main window's style. Nothing on screen says which of the two a row
    /// should wear, and a lit row with neither reads as a row that is simply not the one you are standing
    /// in.</para>
    /// </remarks>
    [Fact]
    public void The_open_row_lights_its_tile_and_only_one_way()
    {
        var withFace = Build(
            [Peer("A", "Anna")],
            shelves: ["A"],
            own: new PeerFace("Giovanni", new Uri("https://api.rpmrpmrpm.com/profiles/g/avatar")));

        // A photograph takes the ring, so the face is not painted over.
        Assert.True(withFace[0].IsOpen);
        Assert.Equal(Visibility.Visible, withFace[0].LitRingVisibility);
        Assert.Equal(Visibility.Collapsed, withFace[0].LitFillVisibility);

        var without = Build([Peer("A", "Anna")], shelves: ["A"], own: new PeerFace("Giovanni", null));

        // No photograph, so the tile itself is the thing lit.
        Assert.True(without[0].IsOpen);
        Assert.Equal(Visibility.Visible, without[0].LitFillVisibility);
        Assert.Equal(Visibility.Collapsed, without[0].LitRingVisibility);
    }

    /// <summary>A row that is not the one on screen is not lit either way, whether or not it has a
    /// face. The pair asks <c>IsOpen</c> first for exactly this.</summary>
    [Fact]
    public void A_row_that_is_not_open_is_not_lit_at_all()
    {
        var rows = Build([Peer("A", "Anna")], shelves: ["A"], viewing: "A");

        var shut = rows.Single(r => !r.IsOpen);
        Assert.Equal(Visibility.Collapsed, shut.LitFillVisibility);
        Assert.Equal(Visibility.Collapsed, shut.LitRingVisibility);
        Assert.Equal(Visibility.Collapsed, shut.OpenVisibility);
    }

    [Fact]
    public void An_account_with_no_picture_keeps_the_house()
    {
        // A name is not a picture. The letter is how somebody ELSE is drawn without one, and an M for
        // "My library" would be the collection's initial pretending to be a person's.
        var rows = Build([Peer("A", "Anna")], shelves: ["A"], own: new PeerFace("Giovanni", null));

        Assert.Equal("\uf015", rows[0].Glyph);
        Assert.Equal(Visibility.Visible, rows[0].GlyphVisibility);
        Assert.Equal(Visibility.Collapsed, rows[0].InitialVisibility);
        Assert.Equal("", rows[0].Initial);
    }

    [Fact]
    public void Nobody_signed_in_is_the_house_as_well()
    {
        // Signed out, or the profile has not landed yet. Both draw the same row, which is why the own
        // face is asked for rather than assumed.
        var rows = Build([Peer("A", "Anna")], shelves: ["A"]);

        Assert.Equal(Visibility.Visible, rows[0].GlyphVisibility);
        Assert.Equal(Visibility.Collapsed, rows[0].PhotoVisibility);
        Assert.Equal(Visibility.Visible, rows[0].OpenVisibility);
    }

    [Fact]
    public void The_person_is_who_the_row_is_named_after_when_one_is_known()
    {
        // A machine can call itself something like HOME-PC-WIN. Nobody thinks of it that way.
        var rows = Build(
            [Peer("A", "GIOVANNI-PC-WIN")],
            shelves: ["A"],
            faces: new Dictionary<string, PeerFace>
            {
                ["A"] = new("Giovanni", new Uri("https://api.rpmrpmrpm.com/profiles/g/avatar")),
            });

        Assert.Equal("Giovanni", rows[1].Name);
        Assert.Equal("G", rows[1].Initial);
        Assert.NotNull(rows[1].Avatar);
    }

    [Fact]
    public void A_computer_with_nobody_behind_it_keeps_its_own_name()
    {
        // Signed out, or an account that never named itself. Either way there is no person to draw.
        var rows = Build([Peer("A", "GIOVANNI-PC-WIN")], shelves: ["A"]);

        Assert.Equal("GIOVANNI-PC-WIN", rows[1].Name);
        Assert.Equal("", rows[1].Initial);
        Assert.Null(rows[1].Avatar);
    }

    [Fact]
    public void A_name_that_opens_on_a_surrogate_pair_is_not_cut_in_half()
    {
        var rows = Build(
            [Peer("A", "PC")],
            shelves: ["A"],
            faces: new Dictionary<string, PeerFace> { ["A"] = new("\U0001F3B8 Anna", null) });

        Assert.Equal("\U0001F3B8", rows[1].Initial);
    }

    [Fact]
    public void Two_computers_of_one_person_say_which_is_which()
    {
        // Both rows would otherwise read the same name over the same face, and picking one would be a
        // guess. The machine's name comes back into the line underneath.
        var giovanni = new PeerFace("Giovanni", null);
        var rows = Build(
            [Peer("A", "GIOVANNI-PC-WIN"), Peer("B", "GIOVANNI-MBP"), Peer("C", "ANNA-PC")],
            shelves: ["A", "B", "C"],
            here: ["A", "C"],
            faces: new Dictionary<string, PeerFace>
            {
                ["A"] = giovanni,
                ["B"] = giovanni,
                ["C"] = new("Anna", null),
            });

        Assert.Equal($"GIOVANNI-PC-WIN ({Here})", rows[1].Status);
        Assert.Equal($"GIOVANNI-MBP ({Away})", rows[2].Status);

        // This name belongs to one person only, so it says nothing about a machine.
        Assert.Equal(Here, rows[3].Status);
    }

    [Fact]
    public void Peers_keep_the_order_they_were_paired_in()
    {
        var rows = Build(
            [Peer("A", "Anna"), Peer("B", "Bram"), Peer("C", "Cato")],
            shelves: ["A", "B", "C"]);

        Assert.Equal(["Anna", "Bram", "Cato"], rows.Skip(1).Select(r => r.Name));
    }

    // ── the display name, from either of the two places it can come from ──

    [Fact]
    public void A_shelf_from_a_machine_that_broadcast_a_person_is_labelled_with_the_person()
    {
        // No profile has been fetched: offline, or it simply has not landed. The display name the
        // machine put in its own beacon is what is left, and it beats the hostname.
        var rows = Build([Peer("A", "GIOVANNI-PC-WIN", face: "Giovanni")], shelves: ["A"]);

        Assert.Equal("Giovanni", rows[1].Name);
    }

    [Fact]
    public void The_profile_still_wins_over_what_they_broadcast()
    {
        // Renamed since their beacon last went out. The account is the live copy.
        var rows = Build(
            [Peer("A", "GIOVANNI-PC-WIN", face: "Gio")],
            shelves: ["A"],
            faces: new Dictionary<string, PeerFace> { ["A"] = new("Giovanni", null) });

        Assert.Equal("Giovanni", rows[1].Name);
    }

    [Fact]
    public void Two_machines_broadcasting_one_person_are_told_apart_by_their_hostnames()
    {
        // The namesake rule looks at fetched profiles and at the broadcast name. Otherwise a collision
        // caused by the broadcast name would go unnoticed and leave two identical rows.
        var rows = Build(
            [Peer("A", "GIO-LAPTOP", face: "Giovanni"), Peer("B", "GIO-DESKTOP", face: "Giovanni")],
            shelves: ["A", "B"],
            here: ["A"]);

        Assert.Equal("Giovanni", rows[1].Name);
        Assert.Equal("Giovanni", rows[2].Name);
        Assert.Equal($"GIO-LAPTOP ({Here})", rows[1].Status);
        Assert.Equal($"GIO-DESKTOP ({Away})", rows[2].Status);
    }

    [Fact]
    public void Two_machines_with_the_same_hostname_and_no_person_do_not_stutter()
    {
        // Nothing to disambiguate WITH: the label already is the machine name, so repeating it in
        // the status line would read as a fault rather than as a distinction.
        var rows = Build(
            [Peer("A", "LAPTOP"), Peer("B", "LAPTOP")], shelves: ["A", "B"], here: ["A"]);

        Assert.Equal(Here, rows[1].Status);
        Assert.Equal(Away, rows[2].Status);
    }

    // ── the mark beside a friend's name ──

    [Fact]
    public void A_friend_who_supports_rpm_wears_the_mark()
    {
        var rows = Build(
            [Peer("A", "GIOVANNI-PC-WIN")],
            shelves: ["A"],
            faces: new Dictionary<string, PeerFace>
            {
                ["A"] = new("Giovanni", null, IsSupporter: true),
            });

        Assert.True(rows[1].IsSupporter);
    }

    [Fact]
    public void A_friend_who_does_not_wears_nothing()
    {
        var rows = Build(
            [Peer("A", "GIOVANNI-PC-WIN")],
            shelves: ["A"],
            faces: new Dictionary<string, PeerFace>
            {
                ["A"] = new("Giovanni", null),
            });

        Assert.False(rows[1].IsSupporter);
    }

    /// <summary>
    /// A face that could not be read is not a claim either way. The lookup answers false on an outage, on
    /// purpose. A badge invented from a dropped request would say something about a person the app never
    /// checked.
    /// </summary>
    [Fact]
    public void An_unknown_face_wears_nothing()
    {
        var rows = Build(
            [Peer("A", "GIOVANNI-PC-WIN")],
            shelves: ["A"],
            faces: new Dictionary<string, PeerFace> { ["A"] = PeerFace.Nobody });

        Assert.False(rows[1].IsSupporter);
    }

    /// <summary>Your own row is where you already are, so it never wears the mark. The mark is how somebody
    /// else is recognised.</summary>
    [Fact]
    public void Your_own_row_never_wears_it()
    {
        var rows = Build(
            [Peer("A", "GIOVANNI-PC-WIN")],
            shelves: ["A"],
            faces: new Dictionary<string, PeerFace>
            {
                ["A"] = new("Giovanni", null, IsSupporter: true),
            },
            own: new PeerFace("Giovanni", null, IsSupporter: true));

        Assert.False(rows[0].IsSupporter);
    }
}
