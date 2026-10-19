using RPM.Desktop.Services.Account;

namespace RPM.Desktop.Presentation.NowPlaying;

/// <summary>
/// Which built-in backdrop is current.
///
/// <para>The default is RPaMp's ground (scripts/make_rpamp_ground.py). Three places must agree on it: the
/// shell's full-window image, the brush the settings windows are painted with, and the Default tile in
/// Settings. So the choice is made here and nowhere else.</para>
///
/// <para>A picture the user chose from disk wins over everything. A built-in they named wins over the
/// default. <see cref="Resolve"/> is the one place the three are ranked.</para>
///
/// <para><b>One of them is for supporters.</b> <see cref="Resolve"/> takes the account's reach with no
/// default for it, because a stored id outlives the support that bought it: a gate on the picker alone
/// would leave a lapsed supporter on gold. Making it an argument makes every call site answer.</para>
/// </summary>
internal static class Backdrop
{
    /// <summary>The player's ground: bands of cyan light in deep blue, drawn by
    /// scripts/make_rpamp_ground.py. RPaMp's four panels use the same picture. The default.</summary>
    public const string RPaMp = "ms-appx:///Assets/background-rpamp.jpg";

    /// <summary>The table of records shot by two lamps at night (scripts/make_vinyl_table.py).</summary>
    public const string Night = "ms-appx:///Assets/background-night.jpg";

    /// <summary>The Pegasus visualizer's empty room against a red record. See GalleryShoot
    /// (<c>--shoot=pegasus:room</c>).</summary>
    public const string Pegasus = "ms-appx:///Assets/background-pegasus.jpg";

    /// <summary>The night table pressed in gold (<c>make_vinyl_table.py gold</c>). Supporters
    /// only.</summary>
    public const string Gold = "ms-appx:///Assets/background-gold.jpg";

    /// <summary>A printed score in warm sepia, sharp across the middle (scripts/make_sheet_music.py, from a
    /// Pexels photograph named in scripts/backdrop-photos.json).</summary>
    public const string SheetMusic = "ms-appx:///Assets/background-sheet-music.jpg";

    /// <summary>What a stored choice looks like. Null or empty is the default.</summary>
    public const string NightId = "night";
    public const string PegasusId = "pegasus";
    public const string GoldId = "gold";
    public const string SheetMusicId = "sheet-music";

    /// <summary>A stored id that means the default. It is read as the default, so the Default tile is lit and
    /// nothing is rewritten.</summary>
    public const string RPaMpId = "rpamp";

    public static string BuiltIn => RPaMp;

    /// <summary>
    /// The picture a stored id names. An unrecognised id gives the default, so a settings file from a later
    /// version still shows a backdrop. A supporter's backdrop asked for by an account that is not one also
    /// gives the default.
    /// </summary>
    public static string Resolve(string? id, SupporterReach reach) => EffectiveId(id, reach) switch
    {
        NightId => Night,
        PegasusId => Pegasus,
        SheetMusicId => SheetMusic,
        GoldId => Gold,
        _ => BuiltIn,
    };

    /// <summary>
    /// Which backdrop is on screen, given what is stored and what the account may have. The empty string
    /// (the default's id) for anything this account cannot be shown.
    ///
    /// <para>Separate from <see cref="Resolve"/> because the shell needs a picture and the Settings row
    /// needs to know which tile to light, and the two must not disagree.</para>
    ///
    /// <para>The stored value is never changed here. When support lapses the picture goes to the default;
    /// when support comes back the stored choice is shown again.</para>
    /// </summary>
    public static string EffectiveId(string? id, SupporterReach reach) => id switch
    {
        NightId => NightId,
        PegasusId => PegasusId,
        SheetMusicId => SheetMusicId,
        RPaMpId => "",
        GoldId when reach.MaySupporterBackdrop => GoldId,
        _ => "",
    };

    /// <summary>A built-in the user can pick, for the row of tiles in Settings.</summary>
    /// <param name="id">What is stored. Empty = the default.</param>
    /// <param name="nameKey">The resource key
    /// for the tile's caption.</param>
    /// <param name="supporters">Whether this one is for supporters
    /// only.</param>
    internal sealed class Preset(string id, string nameKey, bool supporters = false)
    {
        /// <summary>What <see cref="Services.Shell.SettingsService.BuiltInBackground"/> holds for this
        /// one.</summary>
        public string Id { get; } = id;

        public string NameKey { get; } = nameKey;

        /// <summary>Marked in the row, and refused to other accounts with a dialog that says why. The tile
        /// is still shown so users know it exists.</summary>
        public bool Supporters { get; } = supporters;

        /// <summary>The tile's picture, the real one for every account.</summary>
        public string Uri => Id switch
        {
            NightId => Night,
            PegasusId => Pegasus,
            SheetMusicId => SheetMusic,
            GoldId => Gold,
            _ => BuiltIn,
        };
    }

    public static IReadOnlyList<Preset> Presets =>
    [
        new("", "Settings_BackgroundDefault"),
        new(NightId, "Settings_BackgroundNight"),
        new(PegasusId, "Settings_BackgroundPegasus"),
        new(SheetMusicId, "Settings_BackgroundSheetMusic"),
        new(GoldId, "Settings_BackgroundGold", supporters: true),
    ];
}
