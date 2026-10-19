using Microsoft.Extensions.Logging;

namespace RPM.Desktop.Services.Platform;

/// <summary>
/// Captures the reason the OpenGL visualizer failed to start. <c>GLCanvasElement</c> only knows that
/// <c>IsGLInitialized</c> became false. The real cause, such as a missing ANGLE native library, an EGL
/// error or a Metal fallback, is logged by Uno's GL bridge. This sink takes those log lines into the app,
/// so the visualizer readout can show the real message instead of a guess.
/// </summary>
public static class GlDiagnostics
{
    /// <summary>The most recent GL-related warning/error captured, or null if none.</summary>
    public static string? Last { get; private set; }

    /// <summary>Raised whenever <see cref="Last"/> changes. Handlers run on the logging thread, so a
    /// handler must move to the UI thread itself.</summary>
    public static event Action? Changed;

    internal static void Record(string message)
    {
        Last = message;
        Changed?.Invoke();
    }
}

/// <summary>A logging provider that forwards GL-bridge warnings and errors to <see cref="GlDiagnostics"/>.
/// Registered in the host's logging builder.</summary>
public sealed partial class GlDiagnosticsLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new CategoryLogger(categoryName);

    public void Dispose() { }

    private sealed class CategoryLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // Warnings and errors only. Routine per-frame messages never reach here.
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel < LogLevel.Warning)
                return;

            string message = formatter(state, exception);
            string combined = exception is null
                ? message
                : $"{message} :: {exception.GetType().Name}: {exception.Message}";

            // Keep only messages about the GL/ANGLE bridge, so an unrelated warning is not reported
            // as a problem with the visualizer.
            if (!LooksGlRelated(category) && !LooksGlRelated(combined))
                return;

            GlDiagnostics.Record(combined);
        }

        private static bool LooksGlRelated(string text) =>
            text.Contains("Graphics3DGL", StringComparison.OrdinalIgnoreCase)
            || text.Contains("EGL", StringComparison.OrdinalIgnoreCase)
            || text.Contains("ANGLE", StringComparison.OrdinalIgnoreCase)
            || text.Contains("OpenGL", StringComparison.OrdinalIgnoreCase)
            || text.Contains("GLCanvas", StringComparison.OrdinalIgnoreCase);
    }
}
