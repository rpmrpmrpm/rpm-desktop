using Microsoft.ML.OnnxRuntime;

namespace RPM.Onnx;

/// <summary>
/// Opens every <see cref="InferenceSession"/> in the app. RPM018 reports a session built anywhere else.
///
/// <para><b>The options must stay alive until the session exists.</b> <see cref="InferenceSession"/>
/// gives the native <c>CreateSession</c> call the options as a plain pointer and keeps no reference to
/// the <see cref="SessionOptions"/> object. When the caller keeps none either
/// (<c>new InferenceSession(path, new SessionOptions { ... })</c>), the garbage collector can finalise
/// the options while the native call still reads them, and the process crashes inside
/// <c>CreateSession</c>. Loading a model takes long enough for a collection to happen in between. The
/// x64 app under Rosetta crashed this way on every start.</para>
///
/// <para>The session copies what it needs from the options, so they are disposed here once it is open,
/// and also when the model does not open. A caller must not use the options again.</para>
/// </summary>
public static class OnnxSession
{
    /// <summary>
    /// Opens the model at <paramref name="path"/> with <paramref name="options"/>, then disposes the
    /// options. Throws what the runtime throws when the model does not open.
    /// </summary>
    public static InferenceSession Open(string path, SessionOptions options)
    {
        // The using block is what keeps the options reachable: Dispose is a call on them after the
        // constructor returns.
        using (options)
            return new InferenceSession(path, options);
    }
}
