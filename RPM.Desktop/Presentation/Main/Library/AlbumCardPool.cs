using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace RPM.Desktop.Presentation.Main.Library;

/// <summary>
/// The pool of the album wall's card template (<c>CardTemplate</c> in <c>AlbumWallView.xaml</c>). Every row's
/// repeater gives its cards back to this pool and takes cards from it, so a row that comes into view reuses
/// cards that another row gave back.
/// </summary>
/// <remarks>
/// <para>The framework's own <see cref="RecyclePool"/> records which repeater gave a card back. When a different
/// repeater takes that card, the pool looks for the card in the recorded repeater's <c>Children</c> to remove
/// it, and throws "ItemsRepeater's child not found in its Children collection" when it is not there.</para>
///
/// <para>A row's repeater lays out its cards with a <see cref="NonVirtualizingLayout"/>
/// (<see cref="AlbumCardRowLayout"/>). When such a repeater is given a new <c>ItemsSource</c>, which is what
/// <c>AlbumRow.Deal</c> does, it gives every card back to the pool and then empties its <c>Children</c>. So
/// the pool holds cards recorded under a repeater that no longer contains them. If another row takes one of
/// them before the first row takes them back (a row that holds more cards than before, or a row that has just
/// been created), the framework's pool throws inside the wall's layout pass.</para>
///
/// <para>This pool reads where a card is from the visual tree when the card is taken, not from the record of
/// who gave it back. See <see cref="ReturnedCards{TCard}"/>.</para>
///
/// <para><b>Uno only.</b> The Windows App SDK offers <c>RecyclePool</c> only in its experimental release, so
/// the WinUI head does not have the class and keeps the framework's own pool there.</para>
/// </remarks>
#if !RPM_WINUI
internal sealed class AlbumCardPool : RecyclePool
{
    private readonly ReturnedCards<UIElement> _returned = new(
        parentOf: card => VisualTreeHelper.GetParent(card) as Panel,
        removeFrom: (panel, card) => ((Panel)panel).Children.Remove(card));

    protected override void PutElementCore(UIElement element, string key, UIElement? owner)
        => _returned.Put(element, key, owner);

    protected override UIElement? TryGetElementCore(string key, UIElement? owner)
        => _returned.Take(key, owner);

    /// <summary>
    /// The cards given back to the pool, and the choice of which one to hand out. It works on any card type, so
    /// the choice can be tested without the UI.
    /// </summary>
    /// <param name="parentOf">The panel the card is in now, or null when it is in none.</param>
    /// <param name="removeFrom">Removes the card from that panel.</param>
    internal sealed class ReturnedCards<TCard>(Func<TCard, object?> parentOf, Action<object, TCard> removeFrom)
        where TCard : class
    {
        private readonly List<(TCard Card, string ReuseKey, object? Owner)> _returned = [];

        /// <summary>Keeps a card that <paramref name="owner"/> gave back.</summary>
        public void Put(TCard card, string reuseKey, object? owner) => _returned.Add((card, reuseKey, owner));

        /// <summary>
        /// Hands a card to <paramref name="owner"/>, or null when there is none for the key. A card that is in
        /// another panel is removed from it. A card in no panel is returned as it is, and the repeater that asked
        /// adds it to its own children.
        /// </summary>
        public TCard? Take(string reuseKey, object? owner)
        {
            // A row takes its own cards back first: they are already where it needs them. Then a card nobody
            // owns, then the card given back last. This is the framework pool's order.
            var at = _returned.FindIndex(r => r.ReuseKey == reuseKey && (r.Owner is null || ReferenceEquals(r.Owner, owner)));
            if (at < 0)
                at = _returned.FindLastIndex(r => r.ReuseKey == reuseKey);
            if (at < 0)
                return null;

            var card = _returned[at].Card;
            _returned.RemoveAt(at);
            // Where the card is now, not the recorded owner: the owner may have emptied its children since.
            if (parentOf(card) is { } parent && !ReferenceEquals(parent, owner))
                removeFrom(parent, card);
            return card;
        }
    }
}
#endif
