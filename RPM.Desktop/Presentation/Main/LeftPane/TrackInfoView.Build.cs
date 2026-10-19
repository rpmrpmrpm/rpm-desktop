using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using RPM.Core.Library;
using RPM.Core.Lyrics;
using RPM.Core.Metadata.Providers;
using RPM.Desktop.Presentation.Controls;

namespace RPM.Desktop.Presentation.Main.LeftPane;

public sealed partial class TrackInfoView
{
    // ══════════════════════════════════════════════════════════════════════════════════════════════
    //  The pane is rebuilt, not patched
    // ══════════════════════════════════════════════════════════════════════════════════════════════
    //
    //  Six errands fill this pane. Each one has its own clock. The mirror's row is in memory. The file's
    //  tags are a disk read. The cover is a disk read or a cloud fetch. The artist's photograph takes two
    //  network round trips. The timed lyric takes one. MusicBrainz answers one request a second. Each answer
    //  would be painted as it arrived, guarded only by "is this still the record on screen". That check
    //  guards against stale data, but it says nothing about order.
    //
    //  The cover is local and the photograph is not. So for about a second on every change of track, the new
    //  album's cover sat on the last artist's photograph. Two records were on one screen, and no single
    //  arrival was wrong.
    //
    // So the arrivals no longer paint. They hand what they found to the build and ask the pane to look
    // again.
    //  The order in which layers go up belongs to PaneSequence. It is a type with no elements in it, so the
    //  rule can be read and tested on its own.
    //
    //  The sequence
    //
    //  PEEL, bottom to top, when a record is put up: the credits, the links, the album, the members, the
    //  artist, the lyric sheet, the hero, and last the photograph and the ground. The pane rolls up from
    // underneath. The two things that say which record this is, the title and the picture, are the last
    // to go.
    //
    // Unless it is the same artist, which is most changes of track. Then the photograph, the ground, the
    // artist
    //  card and the members are not touched at all: not peeled, not re-fetched, not revealed. See PaneScope.
    //  What the pane says about a person is still true when the next song starts.
    //
    // BUILD ONE, once the cover and the photograph have both answered (with a picture or with "there is
    // none"):
    //  the hero, then the photograph and the ground. Bottom to top, faded.
    //
    // BUILD TWO, once the tags, the encyclopedias and the lyric have all answered: the credits, then the
    // links
    // and the three cards together, and last the lyric sheet. These arrive rather than fade. A card grows
    // into
    //  its place, which is what MoveCard does.
    //
    //  The wait
    //
    // One record turns, never two. It sits top right while the pictures are loading, and in the middle of
    // the
    //  empty text region while the words are. It is the same control the library's track lists use. It is 52
    //  points across, which is why there is only ever one.
    //
    //  Nothing here is a storyboard
    //
    //  Every fade is a ChromeFade, driven off the composition tick. The reason is written on that class. A
    //  dependent animation that is asked to change direction reads the BASE opacity, not the live one, so a
    // layer interrupted mid-peel would snap. The stagger is a DispatcherTimer. It is a clock, not an
    // animation.

    /// <summary>Which record is turning. There is only ever one.</summary>
    private enum PaneWait
    {
        None,
        Pictures,
        Words,
    }

    /// <summary>
    /// One rebuild of the pane: the order it goes up in, and everything the layers are made of.
    ///
    /// <para>It is the only door to the layers. An arrival hands its result to this build and asks the
    /// pane to look again. It does not touch an element. So the order is a property of this type, not of
    /// whichever errand finished last. No call paints out of turn, because no call paints
    /// directly.</para>
    /// </summary>
    private sealed class PaneBuild(string path, PaneScope scope)
    {
        /// <summary>The record this build is for.</summary>
        public string Path { get; } = path;

        /// <summary>The order, and how much of the pane is being replaced. The sequence decides WHEN each
        /// layer goes up and HOW MUCH of the pane is replaced.</summary>
        public PaneSequence Order { get; } = new(scope);

        // The data the layers are made of

        public List<TrackInfoSection> Sections { get; private set; } = [];
        public string Title { get; private set; } = "";
        public string Subtitle { get; private set; } = "";
        public string PlainLyrics { get; private set; } = "";

        public ImageSource? SleeveArt { get; private set; }

        public ImageSource? PhotoArt { get; private set; }

        /// <summary>The photo's aspect when <see cref="PhotoArt"/> is a <see cref="RunOff"/> canvas, and
        /// null when it is the photo itself.</summary>
        public double? PhotoRunOff { get; private set; }

        public string? PhotoArtist { get; private set; }
        public string? PhotoUrl { get; private set; }
        public Windows.UI.Color? Tint { get; private set; }
        public Windows.UI.Color? Behind { get; private set; }

        /// <summary>The blurred copy, already decoded. It is a bitmap and not the bytes, because the
        /// layer goes up in one synchronous step. A decode inside the reveal would be one more arrival to
        /// order, and the pane would paint whenever the decode happened to finish.</summary>
        public ImageSource? WashArt { get; private set; }

        /// <summary>Whether the sleeve already on screen belongs to this record too. That is the next
        /// track on the same album, which is most changes of track. The picture is not fetched, not
        /// decoded and not faded. Only the words beside it change.</summary>
        public bool SleeveKept { get; private set; }

        /// <summary>The same record as the sleeve on screen. An answer, and the cheapest one there
        /// is.</summary>
        public void KeepSleeve()
        {
            SleeveKept = true;
            Order.Answered(PaneErrand.Sleeve, found: true);
        }

        /// <summary>Whether the photograph already on screen belongs to this record too. That is the next
        /// track by the same artist, which is most changes of track. There is nothing to fetch. The layer
        /// is still put back, because it was taken down with the rest of the pane.</summary>
        public bool PhotoUnchanged { get; private set; }

        public TrackDossier? Dossier { get; private set; }
        public LyricSheet Sheet { get; private set; } = LyricSheet.Empty;

        public void GotFacts(List<TrackInfoSection> sections, string title, string subtitle, string lyrics)
        {
            Sections = sections;
            Title = title;
            Subtitle = subtitle;
            PlainLyrics = lyrics;
            Order.Answered(PaneErrand.Facts, found: true);
        }

        public void GotSleeve(ImageSource art)
        {
            SleeveArt = art;
            Order.Answered(PaneErrand.Sleeve, found: true);
        }

        /// <summary>This record has no sleeve anywhere the app can reach. An answer.</summary>
        public void NoSleeve() => Order.Answered(PaneErrand.Sleeve, found: false);

        public void GotPhoto(ImageSource art, double? runOff, string artist, string url,
                             Windows.UI.Color? tint, Windows.UI.Color? behind, ImageSource? wash)
        {
            PhotoArt = art;
            PhotoRunOff = runOff;
            PhotoArtist = artist;
            PhotoUrl = url;
            Tint = tint;
            Behind = behind;
            WashArt = wash;
            Order.Answered(PaneErrand.Photo, found: true);
        }

        public void KeepPhoto()
        {
            PhotoUnchanged = true;
            Order.Answered(PaneErrand.Photo, found: true);
        }

        /// <summary>Nobody has a photograph of this artist under any of its names. This is the only
        /// answer that justifies taking the strip away.</summary>
        public void NoPhoto() => Order.Answered(PaneErrand.Photo, found: false);

        public void GotPapers(TrackDossier? dossier)
        {
            Dossier = dossier;
            Order.Answered(PaneErrand.Papers, dossier is not null);
        }

        public void GotWords(LyricSheet sheet)
        {
            Sheet = sheet;
            Order.Answered(PaneErrand.Words, sheet.IsSynced);
        }
    }

    // Timing

    /// <summary>How long a layer takes to go, and how far apart the layers are as the pane empties.
    /// Quick: this is the pane getting out of the way, not a transition anybody is meant to watch.</summary>
    private const int PeelFadeMs = 140;
    private const int PeelBeatMs = 45;

    /// <summary>How long a layer takes to come back, and the gap between layers. This is the half that is
    /// looked at, so it is slower and the layers are further apart.</summary>
    private const int BuildFadeMs = 260;
    private const int BuildBeatMs = 90;

    private PaneBuild? _build;
    private DispatcherTimer? _beat;

    /// <summary>The fader for each layer, made once and kept.</summary>
    private ChromeFade[]? _layerFades;

    private ChromeFade Fade(PaneLayer layer)
    {
        _layerFades ??= new ChromeFade[8];
        return _layerFades[(int)layer] ??= new ChromeFade(LayerElement(layer));
    }

    /// <summary>What each layer is on screen. The photograph is three elements: the picture, the blurred
    /// copy behind the whole pane, and the flat colour under that. It is handled on its own in <see
    /// cref="ShowLayer"/>. This method returns the element that the others hang off.</summary>
    private FrameworkElement LayerElement(PaneLayer layer) => layer switch
    {
        PaneLayer.Facts => Sections,
        PaneLayer.Elsewhere => LinksBlock,
        PaneLayer.Album => AlbumBlock,
        PaneLayer.Members => MembersBlock,
        PaneLayer.Artist => ArtistBlock,
        PaneLayer.Lyrics => LyricsBlock,
        PaneLayer.Hero => TitleBlock,
        _ => BandPhotoBox,
    };

    // ══ peeling ══

    /// <summary>
    /// Empties the pane for a new record, bottom layer first.
    ///
    /// <para>Everything that is up goes, including the blocks whose replacement is already known. A pane
    /// that keeps its credits while its photograph changes would show two records. That is the exact
    /// fault this sequence exists to fix. Even the photograph that is staying (the next track by the same
    /// artist) goes down and comes back. So the pane changes once, not in pieces.</para>
    /// </summary>
    private void Peel(PaneBuild build)
    {
        StopBeat();

        // Nothing is out for the pictures on a change of track by the same artist, so nothing spins for
        // them: the wait starts down in the text region, where the questions actually are.
        ShowWait(build.Order.FetchingPictures ? PaneWait.Pictures : PaneWait.Words);

        // Already bare. The pane was taken down when the music stopped (see Bare). The first beat puts a
        // layer up, instead of spending the peel's time taking away what is not there.
        if (build.Order.Peeled)
        {
            Strip(build);
            Advance(build);
            return;
        }

        StepPeel(build);
    }

    /// <summary>
    /// Takes the pane down to nothing at once, with no peel and no fade.
    ///
    /// <para>This is for when the music stops. The empty state is shown over the top of the pane, and
    /// nobody is watching a peel. What matters is what the next record finds. A pane left standing behind
    /// "nothing playing" must be emptied before anything new can go up. Without this, the first thing a
    /// listener saw on pressing play was the last record being taken apart, a layer at a time, behind the
    /// words. Emptied here, the next build starts by putting layers up. See PaneSequence.AlreadyEmpty,
    /// which is how it is told.</para>
    ///
    /// <para>The hero and the photograph keep their room, as they do in <see cref="Strip"/>: their
    /// height is what the sheet is placed against, and the sheet is not on screen either way.</para>
    /// </summary>
    private void Bare()
    {
        StopBeat();

        foreach (var layer in PaneSequence.Layers)
        {
            Fade(layer).To(0, 0);
            if (layer is not (PaneLayer.Hero or PaneLayer.Photo))
                LayerElement(layer).Visibility = Visibility.Collapsed;
        }

        HeroArtFade.To(0, 0);
    }

    private ChromeFade? _heroArtFade;

    /// <summary>The sleeve's own fader. It is separate from the hero's fader, because the sleeve belongs
    /// to the record and the words beside it belong to the track.</summary>
    private ChromeFade HeroArtFade => _heroArtFade ??= new ChromeFade(HeroArt);

    private void StepPeel(PaneBuild build)
    {
        if (!ReferenceEquals(_build, build))
            return;

        if (build.Order.Peel() is not { } layer)
        {
            // One more beat, the length of the last layer's own fade, and then the pane is actually
            // emptied. See Strip: fading a block to nothing is not the same as removing it.
            Beat(PeelFadeMs, () =>
            {
                if (!ReferenceEquals(_build, build))
                    return;
                Strip(build);
                Advance(build);
            });
            return;
        }

        Fade(layer).To(0, PeelFadeMs);
        if (layer == PaneLayer.Hero && !build.SleeveKept)
            HeroArtFade.To(0, PeelFadeMs);

        if (layer == PaneLayer.Photo)
        {
            PaneWashFade.To(0, PeelFadeMs);
            PaneGroundFade.To(0, PeelFadeMs);
        }

        Beat(PeelBeatMs, () => StepPeel(build));
    }

    /// <summary>
    /// Takes the emptied blocks out of the layout. A pane can look empty while its blocks still take up room.
    ///
    /// <para>A block at zero opacity still takes up its room. The peel fades every layer down in order,
    /// but fading is not enough on its own. The lyric well, the top card of the text region, would keep
    /// 150 points of the pane open until <c>BuildLyricWell</c> runs, which is the last step of the
    /// rebuild. So the emptied blocks must also be collapsed. Otherwise the pane would empty from the top
    /// down and close up from the bottom only later, which reads as the top cards being removed last. The
    /// members list has the same problem. It collapses only when the next answer repaints it.</para>
    ///
    /// <para>Done in one pass at the end rather than layer by layer on the way down, because by then
    /// every one of them is already invisible: what changes here is the scroll extent, and nobody can
    /// see a transparent block being taken out from underneath another transparent block.</para>
    ///
    /// <para>The hero and the photograph keep their room. Their height is what the whole sheet is placed
    /// against. If the strip closed between two tracks by the same artist, the layout would jump by 150
    /// points and then back again. <c>LoadBandPhotoAsync</c> is there to prevent that. The pane reads as
    /// empty either way, because both are faded out.</para>
    /// </summary>
    private void Strip(PaneBuild build)
    {
        foreach (var layer in PaneSequence.Layers)
        {
            if (build.Order.Keeps(layer))
                continue;   // the artist's own layers never went down and must not be taken out

            if (LayerElement(layer) is { } element && layer is not (PaneLayer.Hero or PaneLayer.Photo))
                element.Visibility = Visibility.Collapsed;
        }
    }

    // ══ building ══

    /// <summary>
    /// Asks the sequence what is due, and puts it up, one layer per beat.
    ///
    /// <para>This runs on every arrival and after every beat. Calling it twice changes nothing. What goes
    /// up is worked out from what has settled and from what is already up. It never depends on how many
    /// times this has run, or on the order in which the answers came in.</para>
    /// </summary>
    private void Advance(PaneBuild build)
    {
        if (!ReferenceEquals(_build, build))
            return;

        var order = build.Order;

        if (order.Next() is not { } layer)
        {
            // Nothing is due. The wait that shows depends on which half is still out. Once both halves
            // are done, no wait shows.
            ShowWait(order.Done ? PaneWait.None
                   : order.Peeled && order.PicturesSettled ? PaneWait.Words
                   : PaneWait.Pictures);
            return;
        }

        switch (layer)
        {
            case PaneLayer.Hero:
                PaintHero(build);
                ShowLayer(build, PaneLayer.Hero);

                // The pictures are up, so the wait moves off the corner and into the empty text region,
                // where the words will appear.
                ShowWait(PaneWait.Words);
                break;

            case PaneLayer.Photo:
                PaintPhoto(build);
                ShowLayer(build, PaneLayer.Photo);
                break;

            case PaneLayer.Facts:
                // Every question about the text region has an answer by now, so the record stops
                // turning before the first block of it goes up. This is the whole point of gating the
                // second half as one: the credits never appear underneath a spinner.
                ShowWait(PaneWait.None);
                PaintFacts(build);
                ShowLayer(build, PaneLayer.Facts);
                break;

            case PaneLayer.Elsewhere:
                PaintPapers(build);
                for (var block = PaneLayer.Elsewhere; block <= PaneLayer.Artist; block++)
                {
                    if (!build.Order.Keeps(block))
                        ShowLayer(build, block);
                }

                break;

            default:
                PaintWords(build);
                ShowLayer(build, PaneLayer.Lyrics);
                break;
        }

        Beat(BuildBeatMs, () => Advance(build));
    }

    /// <summary>Puts one layer back. The three that carry a picture or a page of type ease in. The text
    /// blocks are written straight to full. What moves them is their own arrival, and a block that fades
    /// AND grows reads as two things happening to it.</summary>
    private void ShowLayer(PaneBuild build, PaneLayer layer)
    {
        build.Order.Put(layer);

        var faded = layer is PaneLayer.Facts or PaneLayer.Hero or PaneLayer.Photo;
        Fade(layer).To(1, faded ? BuildFadeMs : 0);

        if (layer == PaneLayer.Hero)
            HeroArtFade.To(1, build.SleeveKept ? 0 : BuildFadeMs);

        if (layer != PaneLayer.Photo)
            return;

        PaneGroundFade.To(1, BuildFadeMs);

        // Read from the brush, not from the build. The picture may be the one that was already up (see
        // KeepPhoto), and so is its wash. That is a fact about the pane, not about this errand's results.
        if (PaneWashArt.ImageSource is not null)
            PaneWashFade.To(PaneWashStrength, BuildFadeMs);
    }

    // ══ what each layer is made of ══
    //
    // Every one of these is synchronous and takes its material from the build. Nothing here fetches,
    // decodes or waits: by the time a layer is due, everything it needs is already in hand, which is
    // what lets the reveal be a decision about ORDER rather than a race.

    /// <summary>The credits. Put back into the layout as well as filled: <see cref="Strip"/> took the
    /// whole block out when the pane was emptied, room and all.</summary>
    private void PaintFacts(PaneBuild build)
    {
        Sections.ItemsSource = build.Sections;
        Sections.Visibility = build.Sections.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// The sleeve, the title and the artist, and the room the photograph will stand in.
    ///
    /// <para>The strip is opened HERE, a beat before the picture that goes in it. Its height is geometry
    /// that the hero is placed against. Opening it under a hero that is already up would move the title
    /// while the reader is reading it. Both pictures have answered by the time this runs, so the room can
    /// be made for a picture that has not been revealed yet.</para>
    /// </summary>
    /// <summary>The heading as it was tagged, beside the starred copy the block is showing. It is kept
    /// because the block's own text is a DRAWN string. Anything that has to look up this song in a
    /// catalogue needs the name the catalogue knows it by.</summary>
    private string _titleRaw = "";

    private void PaintHero(PaneBuild build)
    {
        // The credit is taken off the heading and kept on the record. The subtitle under it already names
        // everybody on the song, so the title would say the same fact twice in the narrowest column in
        // the app. Anything that needs the file's own words asks the build for them (TitleTagged).
        _titleRaw = TitleAsides.WithoutCredits(build.Title);

        // Masked on the way into the block, as the sheet further down this pane is (see TitleCensor). The
        // line above keeps the words as they were tagged. The lyric lookup falls back to this heading
        // when the build has no title of its own, and a masked name would find no sheet.
        TitleText.Text = TitleCensor.Shown(_titleRaw);
        SubtitleText.Text = build.Subtitle;

        // Left alone when the record is the same. The sleeve was never taken down, so there is nothing to
        // put back. Writing the same bitmap again would be a decode and a repaint for no change.
        if (!build.SleeveKept)
        {
            Cover.Source = build.SleeveArt;
            CoverPlaceholder.Visibility = build.SleeveArt is null ? Visibility.Visible : Visibility.Collapsed;
        }

        // Only when this build arranges the strip. On a change of track by the same artist, the
        // photograph never left the pane. Opening the strip would show an empty 260-point area for an
        // artist who has no picture. The sequence marks the photo layer as up because it never went down,
        // not because a picture exists.
        if (!build.Order.Keeps(PaneLayer.Photo))
            ArrangeHero(hasPhoto: build.Order.Of(PaneErrand.Photo) == Arrival.Landed);
    }

    /// <summary>
    /// The photograph, the pane's colour and the blurred copy behind the whole sheet, one layer,
    /// because they are one picture and there is no state in which two of the three are right.
    /// </summary>
    private void PaintPhoto(PaneBuild build)
    {
        // The picture already up is this record's too. Nothing is written; the layer simply comes back.
        if (build.PhotoUnchanged)
            return;

        if (build.Order.Of(PaneErrand.Photo) == Arrival.Nothing)
        {
            // Nobody has a photograph of this artist. The strip is already closed by PaintHero. What is
            // left is to forget the picture, so nothing stale sits under the next one, and to remove the
            // colour. With no picture there is no colour for the pane to match.
            _photoArtist = null;
            _photoUrl = null;
            PutBandPhoto(onA: true, picture: null, runOff: null);
            PutBandPhoto(onA: false, picture: null, runOff: null);
            PhotoFadeA.To(0, 0);
            PhotoFadeB.To(0, 0);
            PaneWashArt.ImageSource = null;
            PaintPanelTint(null, null);
            return;
        }

        // Written straight onto the layer that is showing, not crossfaded with the other one. A crossfade
        // is for two pictures where one replaces the other in front of the reader. Here the pane was
        // emptied first. A dissolve inside a layer that is already fading up would be one fade too many.
        _photoArtist = build.PhotoArtist;
        _photoUrl = build.PhotoUrl;

        var showingFade = _photoOnA ? PhotoFadeA : PhotoFadeB;
        var otherFade = _photoOnA ? PhotoFadeB : PhotoFadeA;

        PutBandPhoto(onA: _photoOnA, build.PhotoArt, build.PhotoRunOff);
        PutBandPhoto(onA: !_photoOnA, picture: null, runOff: null);
        showingFade.To(1, 0);
        otherFade.To(0, 0);

        // The dissolve's depth follows the picture now up; see ShowBandPhoto.
        UpdatePhotoFade((float)PhotoLayers.ActualWidth, (float)PhotoLayers.ActualHeight);

        PaneWashArt.ImageSource = build.WashArt;
        PaintPanelTint(build.Tint, build.Behind);
    }

    /// <summary>The encyclopedia answer: the three cards and the links out. It is one errand, so it is
    /// one step. The cards arrive together, and each one is shown only if the answer filled it. When the
    /// catalogues have nothing, nothing is shown. The blocks then stay collapsed, and the pane reads as
    /// finished rather than as still loading.</summary>
    private void PaintPapers(PaneBuild build)
    {
        if (build.Dossier is { } dossier)
            ShowDossier(dossier);
    }

    /// <summary>
    /// The lyric well: the timed sheet where one was found, and the file's own words where none was.
    ///
    /// <para>Both are written here, in that order. The file's words are not shown first, with the sheet
    /// replacing them later. A replacement would change the length of the well a second after the pane
    /// settled. That is the kind of late arrival this sequence exists to remove.</para>
    /// </summary>
    private void PaintWords(PaneBuild build)
    {
        _sheet = LyricSheet.Empty;
        _lyricsArrive = true;
        PaintLyrics(build.PlainLyrics);

        if (!build.Sheet.IsSynced)
            return;

        _sheet = build.Sheet;
        BuildLyricWell();
    }

    // ══ the one record turning ══

    private PaneWait _waiting = PaneWait.None;

    /// <summary> Which wait is showing. It is one method, not two visibilities, because the spinner is a
    /// 52-point record. Two of them on a pane 320 wide would make the pane look like a pane of spinners.
    /// Naming which ONE is up means a second one cannot be written.
    /// </summary>
    private void ShowWait(PaneWait wait)
    {
        if (_waiting == wait)
            return;
        _waiting = wait;

        PictureBusy.Spinning = wait == PaneWait.Pictures;

        WordsBusy.Spinning = wait == PaneWait.Words;
        BusyRow.Visibility = wait == PaneWait.Words ? Visibility.Visible : Visibility.Collapsed;
    }

    // ══ the beat ══

    /// <summary>One timer for the whole sequence, restarted at each step. A step that is superseded never
    /// runs. That happens when the record changed or the peel was started again, because the build it
    /// closed over is no longer the one on screen.</summary>
    private void Beat(int ms, Action then)
    {
        StopBeat();
        _beat = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        _beat.Tick += (_, _) =>
        {
            StopBeat();
            then();
        };
        _beat.Start();
    }

    private void StopBeat()
    {
        _beat?.Stop();
        _beat = null;
    }
}
