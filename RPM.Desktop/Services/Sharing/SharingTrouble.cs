using RPM.Core.Diagnostics;

namespace RPM.Desktop.Services.Sharing;

/// <summary>
/// What the sharing settings say went wrong last, on the machine it happened to.
///
/// <para>A closed set. No case carries text from an exception. Each case is written to the ring under
/// "sharing" where it is caught.</para>
///
/// <para>The two sharing cases on the network do not say why. A <see cref="Fault"/> reads a dropped
/// connection as "No internet connection.", which is wrong for a computer in the same room. Only the
/// cloud case, which is an internet request, carries a <see cref="Fault"/>.</para>
/// </summary>
public abstract record SharingTrouble
{
    private SharingTrouble() { }

    public abstract string StringKey { get; }

    public abstract object[] Args { get; }

    /// <summary>A request from another computer failed while this one was answering it.</summary>
    public sealed record Serving : SharingTrouble
    {
        public override string StringKey => "Sharing_ServeFailed";
        public override object[] Args => [];
    }

    /// <summary>A computer that is on the network would not hand over its library.</summary>
    public sealed record Reading(string Peer) : SharingTrouble
    {
        public override string StringKey => "Sharing_ReadFailed";
        public override object[] Args => [Peer];
    }

    /// <summary>The cloud copies could not be shared with another account.</summary>
    public sealed record Cloud(Fault Fault) : SharingTrouble
    {
        public override string StringKey => "Sharing_CloudFailed";
        public override object[] Args => [Fault];
    }
}
