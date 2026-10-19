using System.Globalization;
using Microsoft.UI.Xaml;
using RPM.Core.Sharing;

namespace RPM.Desktop.Presentation.Main.Transport.Controls;

/// <summary>
/// Whether a borrowed shelf can be opened right now.
///
/// <para>Every caller must state the value. There is no default, because a wrong answer is hard to see. A
/// switch that offers shelves which cannot be opened looks the same as a working one, until it is pressed.</para>
/// </summary>
public enum ShelfAccess
{
    /// <summary>An account is signed in, so another person's collection can be stood in.</summary>
    Open,

    /// <summary>No account is signed in, or this build has no rpm API. A borrowed shelf cannot be opened in
    /// either case. Half of one is records that the lender put in the cloud, and the URL that plays one is
    /// issued to whoever asks.</summary>
    NeedsAccount,
}

/// <summary>
/// One collection the deck's share button can put on screen: this computer's own collection, or a friend's
/// shelf that has been taken off their machine.
/// </summary>
/// <param name="DeviceId">Whose it is, or empty for the user's own.</param>
/// <param name="Name">The label under the tile: the person's name when there is one, and the machine's name
/// when there is not.</param>
/// <param name="Status">Whether that computer is on the network. It is empty for the user's own row, because
/// those records are on this disk and there is nothing about a network to say.</param>
/// <param name="IsOpen">Whether this is the collection currently on screen.</param>
/// <param name="Face">Who it belongs to, or <see cref="PeerFace.Nobody"/>. On the own row this is the account
/// signed in on this machine, and the tile shows that account's picture rather than a house. The row still
/// says "My library". The face shows whose collection is being shown.</param>
[RpmBindable]
public sealed partial record SharedLibraryRow(
    string DeviceId, string Name, string Status, bool IsOpen, PeerFace Face)
{
    /// <summary>The list's own records, which is the row with no computer behind it.</summary>
    public bool IsOwn => DeviceId.Length == 0;

    /// <summary>Their picture, or null when there is nobody or they have not set one.</summary>
    public Uri? Avatar => Face.Avatar;

    /// <summary>The first letter of the name, for the tile when there is a person but no picture. A text element
    /// is used rather than a char, so a name that starts with an emoji or an accented pair is not cut in half.
    ///
    /// <para>The own row never shows a letter, because its name is "My library", not a person's name. A letter
    /// there would be the collection's initial, looking like somebody's. The rule is decided here, so there is
    /// one answer for whether this row has a letter at all.</para></summary>
    public string Initial => Face.IsKnown && !IsOwn
        ? Face.DisplayName[..StringInfo.GetNextTextElementLength(Face.DisplayName, 0)].ToUpperInvariant()
        : "";

    /// <summary>A house for the user's own collection, and a laptop for a friend's. The two are different kinds of
    /// thing, and the tile shows which one before the name is read. The glyph is used when there is no picture
    /// to draw: on the own row, an account with no picture set; on a friend's row, a machine with nobody behind it.</summary>
    public string Glyph => IsOwn ? "\uf015" : "\uf109";

    public Visibility PhotoVisibility => Show(Avatar is not null);

    public Visibility InitialVisibility => Show(Avatar is null && Initial.Length > 0);

    /// <summary>The glyph, for a tile with neither a picture nor a letter: the house on the own row, and a laptop
    /// for a friend's machine with nobody behind it.</summary>
    public Visibility GlyphVisibility => Show(Avatar is null && Initial.Length == 0);

    /// <summary>The bar down the left of the row on screen, and the wash behind it. This is the same pair a selected
    /// row of the data grid uses. The compact look reads it, because that look is drawn as a grid row.
    /// </summary>
    public Visibility OpenVisibility => Show(IsOpen);

    /// <summary>
    /// The tile of the row on screen, filled with the app's gradient.
    /// </summary>
    /// <remarks>
    /// <b>Filled, except under a photograph.</b> In that case <see cref="LitRingVisibility"/> draws a ring round
    /// the tile instead. A fill over a face would cover the thing the tile is there to show. The two are
    /// exclusive: both check <see cref="IsOpen"/> and disagree about the avatar. So a row that is on screen always
    /// shows exactly one of them.
    ///
    /// <para>Only the roomy look uses this. The compact look draws the row as a grid row and marks the open row
    /// with a wash and a bar, as the grid does. A 16-point tile has no room for a gradient.</para>
    /// </remarks>
    public Visibility LitFillVisibility => Show(IsOpen && Avatar is null);

    /// <summary>The ring round the tile of the row on screen, used where a fill would cover a face. See
    /// <see cref="LitFillVisibility"/> for why the two are used.</summary>
    public Visibility LitRingVisibility => Show(IsOpen && Avatar is not null);

    /// <summary>
    /// Whether this row takes the darker of the two row fills.
    /// </summary>
    /// <remarks>
    /// <see cref="Build"/> sets this from the row's position. Nothing else in the list knows the position. An
    /// ItemsControl does not alternate row fills by itself, and the template gets a row, not an index.
    /// </remarks>
    public bool IsBanded { get; init; }

    public Visibility StatusVisibility => Show(Status.Length > 0);

    /// <summary>
    /// The mark beside a friend's name when they support rpm.
    ///
    /// <para>It is the friend's status, not this machine's. It comes from the profile that the lookup returned,
    /// so it says something about the person whose shelf this row leads to. The badge on the settings pages shows
    /// a different thing: whether YOU support rpm. That badge reads your own status, so it cannot be used here.</para>
    ///
    /// <para>It is never shown on the own row. The user does not need telling about their own collection. The
    /// badge is there to recognise somebody else.</para>
    /// </summary>
    public bool IsSupporter => !IsOwn && Face.IsSupporter;

    private static Visibility Show(bool yes) => yes ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// The collections that can be stood in, own first.
    ///
    /// <para>A friend's shelf counts only once it is actually on this disk. Pairing alone is not enough. Until the
    /// two machines have been on the network together, nothing has been written down, and the wall would open
    /// with no records on it. Another of the user's own computers never counts. Its music was added to this
    /// collection, not kept beside it, so there is nowhere to switch to.</para>
    ///
    /// <para>When signed out, no friend's shelf counts. Opening one needs an account. A list of them would be
    /// places this computer cannot stand in, and the switch on the deck would only ask for a login.</para>
    ///
    /// <para>An empty result means the button has nothing to offer, and it leaves the deck. There is one
    /// exception. If the shelf on screen has just been forgotten or emptied, the own row stays. The way back to
    /// the user's own records must not disappear with the shelf it leads away from.</para>
    /// </summary>
    /// <param name="peers">The computers this one shares with, in the order they were paired.</param>
    /// <param name="access">Whether a borrowed shelf can be opened at all. It must be stated, not defaulted.
    /// Signed out is the usual state of a new install, and it is where a wrong answer shows.</param>
    /// <param name="hasShelf">Whether anything has ever been taken from that computer.</param>
    /// <param name="isHere">Whether that computer is on the network right now.</param>
    /// <param name="faceOf">Who that computer belongs to, as far as is known right now.</param>
    /// <param name="viewingPeer">Whose collection is on screen, or null for the user's own.</param>
    /// <param name="ownFace">Who is signed in on this machine, for the own row's tile. It must be stated, not
    /// defaulted. "Nobody" is not a real answer here. It means signed out, or that the profile has not loaded yet.
    /// A default could be left out without anyone noticing, and then it would be the answer nobody chose.</param>
    public static List<SharedLibraryRow> Build(
        IEnumerable<TrustedPeer> peers,
        ShelfAccess access,
        Func<string, bool> hasShelf,
        Func<string, bool> isHere,
        Func<string, PeerFace> faceOf,
        string? viewingPeer,
        PeerFace ownFace,
        string ownLabel,
        string onlineLabel,
        string offlineLabel)
    {
        ArgumentNullException.ThrowIfNull(peers);
        ArgumentNullException.ThrowIfNull(hasShelf);
        ArgumentNullException.ThrowIfNull(isHere);
        ArgumentNullException.ThrowIfNull(faceOf);
        ArgumentNullException.ThrowIfNull(ownFace);

        // No account is signed in, so there is nowhere to go. The list is emptied here, not later at the moment of
        // a pick. That way the button leaves the deck instead of showing a sign-in prompt.
        var shelves = access is ShelfAccess.Open
            ? peers.Where(p => p.Kind is PeerKind.Guest && hasShelf(p.DeviceId)).ToList()
            : [];

        bool viewing = viewingPeer is { Length: > 0 };

        if (shelves.Count == 0 && !viewing)
            return [];

        List<SharedLibraryRow> rows =
        [
            new("", ownLabel, "", IsOpen: !viewing, ownFace),
        ];

        var faces = shelves.ToDictionary(
            p => p.DeviceId, p => faceOf(p.DeviceId), StringComparer.OrdinalIgnoreCase);

        var labels = shelves.ToDictionary(
            p => p.DeviceId,
            p => PeerLabel.For(faces[p.DeviceId], p.Face, p.Name),
            StringComparer.OrdinalIgnoreCase);

        // One person with two paired computers gives two rows with the same name and the same picture. Nothing on
        // either row says which is which. In that case the computer's own name is shown, ahead of the network state.
        //
        // Namesakes are found using the label that was chosen, not only the profile. A person can be named from two
        // places: the account, and the display name that the machine broadcasts. Two rows can clash through either.
        var namesakes = labels.Values
            .GroupBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);

        rows.AddRange(shelves.Select(p =>
        {
            var face = faces[p.DeviceId];
            var label = labels[p.DeviceId];
            var state = isHere(p.DeviceId) ? onlineLabel : offlineLabel;

            // No need to disambiguate when the label already is the machine name. Repeating it in the status line
            // would look odd.
            bool ambiguous = namesakes.Contains(label)
                             && !string.Equals(label, p.Name, StringComparison.CurrentCultureIgnoreCase);

            return new SharedLibraryRow(
                p.DeviceId,
                label,
                ambiguous ? $"{p.Name} ({state})" : state,
                IsOpen: string.Equals(p.DeviceId, viewingPeer, StringComparison.OrdinalIgnoreCase),
                face);
        }));

        // The rows take the band by position. This is set here, after the list is in order, because only here
        // does a row know its position.
        for (var i = 0; i < rows.Count; i++)
            rows[i] = rows[i] with { IsBanded = i % 2 == 1 };

        return rows;
    }
}
