namespace RPM.Core.Net;

/// <summary>
/// How far this machine can reach, as one of three answers.
///
/// <para>The middle answer is the one that matters most. A laptop on a cafe network that needs a sign-in,
/// or a desktop whose router line is down, still has a working local network. Every peer on that network
/// can be reached, but no service outside it can. Treating this state as offline hides the shared libraries
/// on the network. Treating it as online makes every pass in the app try a name lookup and a socket
/// connection, with retries, and they all fail at once.</para>
/// </summary>
public enum Reach
{
    /// <summary>No network at all. Only what is on this disk.</summary>
    Offline,

    /// <summary>A network, but nothing beyond it answers. This disk and the peers on this LAN.</summary>
    LocalOnly,

    /// <summary>The service answered. Everything is available.</summary>
    Online,
}

/// <summary>The rule that turns the two readings into a Reach value. It is kept apart from the code that
/// takes the readings, so that it can be tested on its own.</summary>
public static class ReachReading
{
    /// <param name="hasNetwork">Whether any interface other than loopback is up.</param>
    /// <param name="internetAnswered">Whether the probe got an answer from outside the LAN. An answer
    /// settles it on its own: a machine whose interfaces cannot be read but whose request came back is
    /// online, whatever the interface list said.</param>
    public static Reach For(bool hasNetwork, bool internetAnswered) =>
        internetAnswered ? Reach.Online
        : hasNetwork ? Reach.LocalOnly
        : Reach.Offline;
}
