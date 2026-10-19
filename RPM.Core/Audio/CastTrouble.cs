namespace RPM.Core.Audio;

/// <summary>
/// What the player says about a cast that failed or changed by itself.
///
/// <para>A closed set of cases. No case carries text from an exception, so the player never shows raw
/// .NET socket text. Where an exception was thrown, it is written to the ring when it is caught
/// (<see cref="Diagnostics.Faults.Note"/>).</para>
///
/// <para>Every case names the speaker, because the message is about one speaker. The UI layer turns a
/// case into words: <see cref="StringKey"/> formatted with <see cref="Device"/>.</para>
/// </summary>
public abstract record CastTrouble
{
    private CastTrouble(string device) => Device = device;

    /// <summary>The speaker's name, as the player lists it.</summary>
    public string Device { get; }

    public abstract string StringKey { get; }

    /// <summary>True when the music keeps playing on the speaker. A notice, not a failure.</summary>
    public virtual bool IsNotice => false;

    /// <summary>The connection to the speaker was lost.</summary>
    public sealed record Disconnected(string Device) : CastTrouble(Device)
    {
        public override string StringKey => "Cast_Disconnected";
    }

    /// <summary>The speaker accepted the stream and never came to fetch it.</summary>
    public sealed record NeverFetched(string Device) : CastTrouble(Device)
    {
        public override string StringKey => "Cast_NeverFetched";
    }

    /// <summary>The speaker refused the stream, or loading it threw.</summary>
    public sealed record StreamRefused(string Device) : CastTrouble(Device)
    {
        public override string StringKey => "Cast_StreamRefused";
    }

    /// <summary>The speaker has not played a frame in <c>CastSink.StallTimeout</c>.</summary>
    public sealed record StoppedPlaying(string Device) : CastTrouble(Device)
    {
        public override string StringKey => "Cast_StoppedPlaying";
    }

    /// <summary>The stream was running and the encoder or the socket under it failed.</summary>
    public sealed record StreamBroke(string Device) : CastTrouble(Device)
    {
        public override string StringKey => "Cast_StreamBroke";
    }

    /// <summary>The box decodes Dolby and is not passing it on right now (an amplifier off or not
    /// selected). Sending stereo.</summary>
    public sealed record DolbyOff(string Device) : CastTrouble(Device)
    {
        public override string StringKey => "Cast_DolbyOff";
        public override bool IsNotice => true;
    }

    /// <summary>The speaker will not play surround. Sending stereo.</summary>
    public sealed record NoSurround(string Device) : CastTrouble(Device)
    {
        public override string StringKey => "Cast_NoSurround";
        public override bool IsNotice => true;
    }

    /// <summary>No answer from the speaker's address: it is off, asleep or on another network.</summary>
    public sealed record Unreachable(string Device) : CastTrouble(Device)
    {
        public override string StringKey => "Cast_Unreachable";
    }

    /// <summary>The speaker answered and the connection still failed.</summary>
    public sealed record ConnectFailed(string Device) : CastTrouble(Device)
    {
        public override string StringKey => "Cast_ConnectFailed";
    }

    /// <summary>The speaker refuses every app because it is in a stereo pair or speaker group.
    /// Casting works only to the group itself.</summary>
    public sealed record InPair(string Device) : CastTrouble(Device)
    {
        public override string StringKey => "Cast_InPair";
    }

    /// <summary>The connection is fine and the receiver would not do as it was asked.</summary>
    public sealed record CommandFailed(string Device) : CastTrouble(Device)
    {
        public override string StringKey => "Cast_CommandFailed";
    }

    /// <summary>The receiver sent an error of its own.</summary>
    public sealed record ReceiverError(string Device) : CastTrouble(Device)
    {
        public override string StringKey => "Cast_ReceiverError";
    }
}
