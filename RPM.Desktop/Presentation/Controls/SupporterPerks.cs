namespace RPM.Desktop.Presentation.Controls;

/// <summary>One benefit of supporting rpm.</summary>
/// <param name="StringKey">The string key, in <c>scripts/strings.json</c>. It is shared with
/// <c>SupporterDialog</c>, which prints the same eight.</param>
/// <param name="Glyph">The Font Awesome code point for the benefit. It is written as an escape, not as the
/// character itself, because raw private-use characters do not survive every editor that touches this
/// file.</param>
/// <param name="Gold">Whether it is drawn in supporter gold, not the app's cyan. It is true only for the
/// two perks that are gold, so the colour means something and is not just decoration.</param>
public readonly record struct SupporterPerk(string StringKey, string Glyph, bool Gold = false);

/// <summary>What supporters get, as one list.
///
/// <para><b>Why a list and not a shared control.</b> Two surfaces show this, and they have different
/// shapes. <c>SupporterDialog</c> is a narrow column and prints the list with ticks. The last step of the
/// setup wizard has a card to fill, and it prints the list as a grid of tiles with the glyph above the
/// label. One control would force one of those layouts on the other. What must not be written twice is the
/// CONTENT. That is what is here: the same eight, in the same order, named by the same string keys.</para>
///
/// <para>The price is not in this list. It is <c>Supporter_Price</c>, and both surfaces read it directly.
/// So neither can quote a figure that the other does not.</para>
/// </summary>
public static class SupporterPerks
{
    /// <summary>The eight perks, in the order that both surfaces show them.
    /// </summary>
    /// <remarks><b>The heart belongs to the badge.</b> <see cref="SupporterBadge"/> says <c>\uf004</c> is
    /// the mark that every supporter item wears. So the heart goes on the badge perk and nowhere else in
    /// this list. Keeping the community running takes the group glyph instead. Putting the heart on both
    /// would make the badge's own mark mean two things on one card.
    /// </remarks>
    public static readonly IReadOnlyList<SupporterPerk> All =
    [
        new("Supporter_PerkStorage",     "\uf0c2"),               // cloud
        new("Supporter_PerkBitrate",     "\uf8f1"),               // waveform
        new("Supporter_PerkRecognition", "\uf577"),               // fingerprint
        new("Supporter_PerkDj",          "\uf1de"),               // sliders
        new("Supporter_PerkKaraoke",     "\uf130"),               // microphone
        new("Supporter_PerkBackdrop",    "\uf53f", Gold: true),   // palette
        new("Supporter_PerkBadge",       "\uf004", Gold: true),   // heart, the supporter mark
        new("Supporter_PerkCommunity",   "\uf0c0"),               // people
    ];
}
