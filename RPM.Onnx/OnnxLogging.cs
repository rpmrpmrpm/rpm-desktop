using Microsoft.ML.OnnxRuntime;

namespace RPM.Onnx;

/// <summary>
/// Turns the ONNX Runtime's own logging down to errors, before the first model is opened.
///
/// <para>Every model the app ships triggers the runtime's graph checker on load, and each one writes
/// warnings to stderr: over a hundred lines per session like "Initializer <c>regression_headers.1.2.weight</c>
/// appears in graph inputs and will not be treated as constant value/weight". This is about how the
/// models were exported, not an error: an initializer left in the input list can still be overridden,
/// so the runtime does not const-fold it. The models load and infer correctly either way.</para>
///
/// <para>The runtime writes these through its ENVIRONMENT logger, a process-wide singleton created
/// lazily at <c>ORT_LOGGING_LEVEL_WARNING</c> the first time anything uses it. It can be configured
/// only before it exists (<see cref="OrtEnv.CreateInstanceWithOptions"/> throws once the singleton is
/// created), so every session must call this first. It is cheap and idempotent.</para>
///
/// <para>To remove the warnings at the source, the runtime ships
/// <c>onnxruntime/tools/python/remove_initializer_from_input.py</c>, which rewrites a model so its weights
/// are no longer inputs. That also enables const folding again. Do it when the models are next
/// exported (see scripts/fetch-models.sh); it is not a reason to ship new models by itself.</para>
/// </summary>
public static class OnnxLogging
{
    private static readonly object Lock = new();
    private static bool _configured;

    /// <summary>Called by every service that is about to build an <see cref="InferenceSession"/>.</summary>
    public static void Quieten()
    {
        lock (Lock)
        {
            if (_configured)
                return;
            _configured = true;

            try
            {
                // The environment already exists, because a session was created first. Nothing to do,
                // and no need to log: the warnings are harmless either way.
                if (OrtEnv.IsCreated)
                    return;

                var options = new EnvironmentCreationOptions
                {
                    logLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
                };
                OrtEnv.CreateInstanceWithOptions(ref options);
            }
            catch
            {
                // A quieter log is not a reason to fail a model load.
            }
        }
    }
}
