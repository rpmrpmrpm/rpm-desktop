using RPM.Core.Diagnostics;

namespace RPM.Desktop.Services.Cloud;

/// <summary>
/// The problem that the cloud status line reports most recently.
///
/// <para>A closed set. Each case holds only plain values. No case carries text from an exception:
/// a failure is a <see cref="Fault"/>, and the exception itself is written to the log where it was
/// caught.</para>
///
/// <para>The settings page turns one into words. <see cref="StringKey"/> is formatted with
/// <see cref="Args"/>, and a <see cref="Fault"/> argument becomes a sentence of its own. See
/// <c>CloudSettingsPage.Say</c>.</para>
/// </summary>
public abstract record CloudTrouble
{
    private CloudTrouble() { }

    public abstract string StringKey { get; }

    public abstract object[] Args { get; }

    /// <summary>The store is full. The state clears when the quota check reports free space
    /// again.</summary>
    public sealed record StorageFull : CloudTrouble
    {
        public override string StringKey => "Cloud_StorageFull";
        public override object[] Args => [];
    }

    /// <summary>None of the tracks that the library expects are in the cloud store.</summary>
    public sealed record ManyTracksMissing : CloudTrouble
    {
        public override string StringKey => "Cloud_ManyTracksMissing";
        public override object[] Args => [];
    }

    /// <summary>Some cloud-only tracks could not be downloaded.</summary>
    public sealed record BringBackFailed(int Failed, int Of) : CloudTrouble
    {
        public override string StringKey => "Cloud_BringBackFailed";
        public override object[] Args => [Failed, Of];
    }

    /// <summary>A sync pass stopped before it finished.</summary>
    public sealed record SyncStopped(Fault Fault) : CloudTrouble
    {
        public override string StringKey => "Cloud_SyncStopped";
        public override object[] Args => [Fault];
    }

    /// <summary>Some tracks could not be removed from the cloud.</summary>
    public sealed record TracksNotRemoved(int Failed, int Of, Fault Fault) : CloudTrouble
    {
        public override string StringKey => "Cloud_TracksNotRemoved";
        public override object[] Args => [Failed, Of, Fault];
    }

    /// <summary>A playlist that should have been removed from the cloud is still there.</summary>
    public sealed record PlaylistNotRemoved(Fault Fault) : CloudTrouble
    {
        public override string StringKey => "Cloud_PlaylistNotRemoved";
        public override object[] Args => [Fault];
    }

    /// <summary>A playlist's order could not be stored.</summary>
    public sealed record PlaylistNotStored(string Name, Fault Fault) : CloudTrouble
    {
        public override string StringKey => "Cloud_PlaylistNotStored";
        public override object[] Args => [Name, Fault];
    }
}
