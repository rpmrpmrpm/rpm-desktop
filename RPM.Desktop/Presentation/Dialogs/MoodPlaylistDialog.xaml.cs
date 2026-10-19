using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using RPM.Core.Library;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Dialogs;

/// <summary>
/// The window where a listener makes a mood playlist of their own, or changes one they made: its
/// name, the mood, and which genres, decades and artists it draws on, and the picture under its cover's frame. What it
/// hands back is <see cref="Chosen"/>; the caller writes the playlist and its cover. This window only reads.
///
/// <para><b>In steps, one section a screen:</b> name and mood, genres and decades, artists, cover. Back and Next
/// move between them; Create (or Save and refill) is on the last. The count under every step is the recipe as it
/// stands, so a narrowing that empties the list is seen on the step that made it. The cover step and what arrives
/// on its own clock there are in
/// <c>MoodPlaylistDialog.Cover.cs</c>.</para>
///
/// <para><b>What arrives on its own clock here, and why each is safe.</b> Every field is written and read on the UI
/// thread. Two things are worked out off it and handed back by an <c>await</c> that started on a UI-thread handler, so
/// their continuations resume on the UI thread.</para>
/// <list type="number">
/// <item><description><b>The session's mood shelf</b> (<see cref="LibraryDatabase.SessionMoods"/>), read once in
/// <c>OnOpened</c>. It is built at most once a session and can take seconds the first time; until it is in, the genre
/// chips show a spinner and Save stays off. Nothing else assigns <c>_shelf</c>, so there is no older read to land on a
/// newer one. Safe.</description></item>
/// <item><description><b>The count</b> (<c>RecountAsync</c>), asked on every change of a choice. Each ask takes a
/// new generation and its answer is shown only if it is still the newest when it comes back, so a slow count for an
/// older recipe cannot overwrite a newer one. Save does not use the count: it hands back the recipe as chosen, and the
/// caller fills from that. Safe because of the generation check; remove it and the count can lag the chips.</description></item>
/// <item><description><b>The user</b>, between any two of these: a chip pressed before the shelf is in only changes
/// the chosen sets, which the shelf's arrival then draws from. Closing while a count is out leaves its answer to
/// land on a window nobody sees, which changes nothing.</description></item>
/// </list>
/// </summary>
public sealed partial class MoodPlaylistDialog : ContentDialog
{
    private readonly LibraryDatabase _db;
    private readonly bool _editing;

    private MoodRecipeMood _mood;
    private readonly HashSet<string> _genres = new(StringComparer.Ordinal);
    private readonly HashSet<int> _decades = [];
    private readonly List<string> _artists = [];

    private MoodShelf? _shelf;
    private int _countGeneration;
    private int _matching;

    /// <summary>
    /// What was chosen, or null when the window was closed without saving: the name, the recipe (its cover choice
    /// included), the framed cover drawn for them, and the listener's own picture unframed when that is what is under
    /// the frame, for the caller to keep so a rename can frame it again.
    /// </summary>
    public (string Name, MoodRecipe Recipe, PlaylistArt.Drawn Cover, byte[]? Under)? Chosen { get; private set; }

    private enum Step { Mood, Filters, Artists, Cover }

    private Step _step = Step.Mood;

    /// <param name="db">The library the list is made in. Read for the mood shelf and the count, never written.</param>
    /// <param name="name">The list's name now; a new list's default name for a new one.</param>
    /// <param name="current">The recipe the list was made from when it is being changed; null for a new list.</param>
    /// <param name="currentPicture">The listener's own picture under the list's frame, read by the caller from where it
    /// keeps it, when <paramref name="current"/>'s cover is one; null otherwise. Handed in so this window reads no
    /// disk of its own.</param>
    public MoodPlaylistDialog(LibraryDatabase db, string name, MoodRecipe? current, byte[]? currentPicture)
    {
        ArgumentNullException.ThrowIfNull(db);
        InitializeComponent();
        _db = db;
        _editing = current is not null;
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        Shell.AttachResize(this, minWidth: 760, minHeight: 520, maxWidth: 1200, defaultHeight: 640, defaultWidth: 940);

        NameBox.Text = name;
        NameBox.SelectAll();
        SaveText.Text = Loc.Get(_editing ? "MoodMaker_Refill" : "MoodMaker_Create");

        _mood = current?.Mood ?? MoodRecipeMood.Calm;
        foreach (var g in current?.Genres ?? MoodRecipe.Everything) _genres.Add(g);
        foreach (var d in current?.Decades ?? []) _decades.Add(d);
        foreach (var a in current?.Artists ?? MoodRecipe.Everything) _artists.Add(a);

        BuildMoodChips();
        BuildDecadeChips();
        BuildArtistChips();
        SetUpCover(current?.Cover ?? MoodCover.OfMood, currentPicture);
        ShowStep();
        Opened += OnOpened;
        Closed += (_, _) => CloseCover();
    }

    // ── the steps ──

    private static readonly Step[] Steps = Enum.GetValues<Step>();

    private void ShowStep()
    {
        StepMood.Visibility = _step is Step.Mood ? Visibility.Visible : Visibility.Collapsed;
        StepFilters.Visibility = _step is Step.Filters ? Visibility.Visible : Visibility.Collapsed;
        StepArtists.Visibility = _step is Step.Artists ? Visibility.Visible : Visibility.Collapsed;
        StepCover.Visibility = _step is Step.Cover ? Visibility.Visible : Visibility.Collapsed;

        var name = Loc.Get(_step switch
        {
            Step.Mood => "MoodMaker_StepMood",
            Step.Filters => "MoodMaker_StepFilters",
            Step.Artists => "MoodMaker_StepArtists",
            Step.Cover => "MoodMaker_StepCover",
        });
        StepText.Text = Loc.Format("MoodMaker_Step", Array.IndexOf(Steps, _step) + 1, Steps.Length, name);

        var last = _step == Steps[^1];
        BackButton.Visibility = _step == Steps[0] ? Visibility.Collapsed : Visibility.Visible;
        NextButton.Visibility = last ? Visibility.Collapsed : Visibility.Visible;
        SaveButton.Visibility = last ? Visibility.Visible : Visibility.Collapsed;
        UpdateSave();

        if (_step is Step.Cover)
            EnterCover();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        var at = Array.IndexOf(Steps, _step);
        if (at > 0)
        {
            _step = Steps[at - 1];
            ShowStep();
        }
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        var at = Array.IndexOf(Steps, _step);
        if (at < Steps.Length - 1)
        {
            _step = Steps[at + 1];
            ShowStep();
        }
    }

    private async void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        NameBox.Focus(FocusState.Programmatic);
        try
        {
            var db = _db;
            _shelf = await Task.Run(() => db.SessionMoods);
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
        {
            // The library closed under the window. Nothing can be counted or made; Save stays off.
            _shelf = null;
        }
        GenresSpinner.Visibility = Visibility.Collapsed;
        MeasuredText.Text = _shelf is { } shelf ? Loc.Format("MoodMaker_Measured", shelf.MeasuredTracks) : "";
        BuildGenreChips();
        await RecountAsync();
    }

    /// <summary>The recipe as chosen so far, with the cover the cover step has chosen (<see cref="CoverChoice"/>).</summary>
    private MoodRecipe Recipe() => new(
        _mood,
        _genres.Count == 0 ? MoodRecipe.Everything : [.. _genres.Order(StringComparer.Ordinal)],
        [.. _decades.Order()],
        _artists.Count == 0 ? MoodRecipe.Everything : [.. _artists],
        CoverChoice());

    // ── the chips ──

    private static string MoodLabel(MoodRecipeMood mood) => Loc.Get(mood switch
    {
        // The app's own names for its mood lists, so this window and the generated lists agree.
        MoodRecipeMood.Calm => "Playlists_Calm",
        MoodRecipeMood.Dancefloor => "Playlists_Danceable",
        MoodRecipeMood.Adrenaline => "Playlists_Energetic",
        MoodRecipeMood.Focus => "Playlists_Focus",
    });

    private void BuildMoodChips()
    {
        MoodChips.Children.Clear();
        foreach (var mood in Enum.GetValues<MoodRecipeMood>())
        {
            MoodChips.Children.Add(Chip(MoodLabel(mood), mood == _mood, pressed =>
            {
                if (_mood == mood)
                {
                    BuildMoodChips();   // A mood is always chosen: pressing the lit one keeps it lit.
                    return;
                }
                _mood = mood;
                BuildMoodChips();
                BuildGenreChips();
                _ = RecountAsync();
            }));
        }
    }

    /// <summary>The genres the chosen mood holds songs of, most first, and any chosen genre it does not, so a choice
    /// is never hidden while it still narrows the list.</summary>
    private void BuildGenreChips()
    {
        GenreChips.Children.Clear();
        if (_shelf is not { } shelf)
            return;
        var held = shelf.ListFor(Recipe().Kind).GenresHeld().Select(g => g.Genre).ToList();
        foreach (var genre in held.Concat(_genres.Where(g => !held.Contains(g)).Order(StringComparer.Ordinal)))
        {
            GenreChips.Children.Add(Chip(genre, _genres.Contains(genre), on =>
            {
                if (on) _genres.Add(genre); else _genres.Remove(genre);
                _ = RecountAsync();
            }));
        }
    }

    private void BuildDecadeChips()
    {
        DecadeChips.Children.Clear();
        foreach (var decade in MoodRecipe.DecadesOffered)
        {
            DecadeChips.Children.Add(Chip(Loc.Format("Genres_Decade", decade), _decades.Contains(decade), on =>
            {
                if (on) _decades.Add(decade); else _decades.Remove(decade);
                _ = RecountAsync();
            }));
        }
    }

    private void BuildArtistChips()
    {
        ArtistChips.Children.Clear();
        foreach (var artist in _artists)
        {
            var remove = new Button
            {
                Style = (Style)Application.Current.Resources["GlassButtonStyle"],
                Padding = new Thickness(12, 0, 12, 0),
                Height = 28,
            };
            ToolTipService.SetToolTip(remove, Loc.Format("MoodMaker_RemoveArtist", artist));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock { Text = artist, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new FontIcon
            {
                Style = (Style)Application.Current.Resources["FaIcon"],
                Glyph = "",
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
            });
            remove.Content = row;
            remove.Click += (_, _) =>
            {
                _artists.Remove(artist);
                BuildArtistChips();
                _ = RecountAsync();
            };
            ArtistChips.Children.Add(remove);
        }
    }

    /// <summary>A toggle chip. <see cref="ToggleButton"/>'s tool style paints no checked face of its own, so the
    /// chip paints it here, as the cover window paints its mark.</summary>
    private ToggleButton Chip(string text, bool on, Action<bool> changed)
    {
        var chip = new ToggleButton
        {
            Content = text,
            IsChecked = on,
            Style = (Style)Application.Current.Resources["ToolToggleStyle"],
            Padding = new Thickness(12, 0, 12, 0),
            Height = 28,
            FontSize = 12,
        };
        PaintChip(chip);
        chip.Click += (_, _) =>
        {
            PaintChip(chip);
            changed(chip.IsChecked == true);
        };
        return chip;
    }

    private void PaintChip(ToggleButton chip) =>
        chip.Background = (Brush)Resources[chip.IsChecked == true ? "ChipLitBrush" : "ChipDarkBrush"];

    // ── artists ──

    private void ArtistBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput || _shelf is not { } shelf)
            return;
        var typed = sender.Text.Trim();
        sender.ItemsSource = typed.Length == 0
            ? null
            : shelf.ListFor(Recipe().Kind).ActsHeld()
                   .Select(a => a.Act)
                   .Where(a => a.Contains(typed, StringComparison.CurrentCultureIgnoreCase)
                               && !_artists.Contains(a, StringComparer.OrdinalIgnoreCase))
                   .OrderBy(a => a.StartsWith(typed, StringComparison.CurrentCultureIgnoreCase) ? 0 : 1)
                   .Take(12)
                   .ToList();
    }

    private void ArtistBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var name = (args.ChosenSuggestion as string ?? sender.Text).Trim();
        if (name.Length == 0 || _artists.Contains(name, StringComparer.OrdinalIgnoreCase))
            return;
        _artists.Add(name);
        sender.Text = "";
        sender.ItemsSource = null;
        BuildArtistChips();
        _ = RecountAsync();
    }

    // ── the count ──

    private async Task RecountAsync()
    {
        if (_shelf is null)
            return;
        var generation = ++_countGeneration;
        var recipe = Recipe();
        var db = _db;
        // Both the songs that match and the songs the list would hold: the list keeps at most MoodRecipe.Songs and
        // takes only so many off one record, so the two differ, and showing only the first promised songs the list
        // does not have (for example 5 matched but 4 are saved).
        int matching, held;
        try
        {
            (matching, held) = await Task.Run(() => (db.MoodRecipePool(recipe).Count, db.MoodRecipeSongs(recipe).Count));
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
        {
            (matching, held) = (0, 0);
        }
        if (generation != _countGeneration)
            return;   // a newer choice has been counted, or is being
        _matching = held;
        CountText.Text = held == 0 ? Loc.Get("MoodMaker_None")
            : held < matching ? Loc.Format("MoodMaker_CountShown", matching, held)
            : matching == 1 ? Loc.Get("MoodMaker_CountOne")
            : Loc.Format("MoodMaker_Count", matching);
        UpdateSave();
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateSave();

    /// <summary>Next needs a name, since the cover is drawn with it; Create needs songs as well.</summary>
    private void UpdateSave()
    {
        var named = !string.IsNullOrWhiteSpace(NameBox.Text);
        NextButton.IsEnabled = named;
        SaveButton.IsEnabled = !_saving && named && _shelf is not null && _matching > 0;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Hide();

    /// <summary>
    /// Draws the cover for what was chosen, then hands everything back. The footer is off while it draws, and a
    /// window dismissed during the draw (Escape gets past disabled buttons) hands back nothing, as the cover window's
    /// Save does: a recipe chosen with no cover drawn for it is the half-written state that rule exists to stop.
    /// </summary>
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_saving || string.IsNullOrWhiteSpace(NameBox.Text))
            return;
        _saving = true;
        CancelButton.IsEnabled = false;
        BackButton.IsEnabled = false;
        UpdateSave();

        var name = NameBox.Text.Trim();
        var recipe = Recipe();
        PlaylistArt.Drawn cover;
        try { cover = await DrawCoverAsync(recipe, name, finalDraw: true); }
        catch { cover = PlaylistArt.Drawn.None; }

        if (_closing.IsCancellationRequested)
            return;   // dismissed while the cover was drawing; nothing is handed back

        Chosen = (name, recipe, cover, recipe.Cover.Source is MoodCoverSource.Picture ? _picture : null);
        Hide();
    }
}
