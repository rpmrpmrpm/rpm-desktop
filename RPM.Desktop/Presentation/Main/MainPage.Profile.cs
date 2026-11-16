namespace RPM.Desktop.Presentation.Main;

/// <summary>
/// What this computer tells the reader's profile on its own: the song playing (<see cref="ListeningShare"/>)
/// and the badges the collection unlocks (<see cref="BadgeReporter"/>). Attached after the first screen, with
/// the other views that read the library.
/// </summary>
public sealed partial class MainPage
{
    private bool _profileHooked;
    private ListeningShare? _listening;
    private BadgeReporter? _badges;

    private void AttachProfileReporters()
    {
        if (_profileHooked || Model is not { } model)
            return;
        _profileHooked = true;

        var listening = _listening = new ListeningShare();
        var playback = App.Services.GetRequiredService<PlaybackService>();
        var radio = App.Services.GetRequiredService<RadioService>();
        // Both report off the UI thread; the share keeps its state on the UI thread.
        playback.Changed += () => DispatcherQueue?.TryEnqueue(listening.Note);
        radio.Changed += () => DispatcherQueue?.TryEnqueue(listening.Note);
        listening.Note();

        var badges = _badges = new BadgeReporter();
        badges.Sent += () => DispatcherQueue?.TryEnqueue(FeedBody.CountUnreadAgain);
        FeedBody.OwnBadges = () => badges.Unlocked;
        model.LibraryAlbumsChanged += OnAlbums;
        Unloaded += (_, _) => model.LibraryAlbumsChanged -= OnAlbums;
        badges.Note(model);

        void OnAlbums() => DispatcherQueue.TryEnqueue(() => badges.Note(model));
    }
}
