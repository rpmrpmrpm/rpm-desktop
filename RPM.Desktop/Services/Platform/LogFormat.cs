using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace RPM.Desktop.Services.Platform;

/// <summary>The console's line format: <c>[tag] what happened</c>, and nothing in front of it.
///
/// <para>Most of this app's tracing uses <c>Said.Line("analysis", $"...")</c>. That writes one line: a tag
/// that says which pass is talking, then the fact. Messages sent through <see cref="ILogger"/> are written
/// in a different shape by default. The type name goes on one line and the message is indented on the next,
/// so ten log calls fill twenty lines. The type is not the useful part. The tag and the sentence are.
///</para>
///
/// <para>Logged messages in the app start with their own tag, and the formatter leaves that tag as it is.
/// A category with no tag of its own, such as one from Uno or another library, gets a tag from the last
/// segment of its name. That segment is the only part that identifies the source.</para>
/// </summary>
internal sealed class RpmConsoleFormatter() : ConsoleFormatter(FormatterName)
{
    /// <summary>The name this formatter is selected by. See <c>App.OnLaunched</c>.</summary>
    public const string FormatterName = "rpm";

    public override void Write<TState>(
        in LogEntry<TState> entry, IExternalScopeProvider? scopeProvider, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        string message = entry.Formatter?.Invoke(entry.State, entry.Exception) ?? string.Empty;
        if (message.Length == 0 && entry.Exception is null)
            return;

        // Debug and information lines carry no level word, as in the Debug.WriteLine tracing. A warning or
        // an error gets a word, so a reader scanning for problems can find it.
        if (entry.LogLevel >= LogLevel.Error)
            writer.Write("fail: ");
        else if (entry.LogLevel == LogLevel.Warning)
            writer.Write("warn: ");

        if (!message.StartsWith('['))
        {
            writer.Write('[');
            writer.Write(ShortTag(entry.Category));
            writer.Write("] ");
        }

        writer.WriteLine(message);

        if (entry.Exception is not null)
            writer.WriteLine(entry.Exception.ToString());
    }

    /// <summary>The last segment of a dotted category name: "Uno.WinUI.Graphics3DGL" reads as
    /// "Graphics3DGL", which is the part that says which thing is talking.</summary>
    private static string ShortTag(string category)
    {
        if (string.IsNullOrEmpty(category))
            return "log";

        int dot = category.LastIndexOf('.');
        return dot >= 0 && dot < category.Length - 1 ? category[(dot + 1)..] : category;
    }
}

/// <summary>Logging setup that has to happen against the service collection rather than the logging
/// builder. See <see cref="RpmConsoleFormatter"/> for what the lines are meant to look like.</summary>
internal static class LogProviders
{
    /// <summary>
    /// Drops the Debug logging provider Uno's <c>UseLogging</c> registers alongside the console one.
    ///
    /// <para>Under a debugger both providers are active, so every line is written twice. The console writes
    /// it in the shape that <see cref="RpmConsoleFormatter"/> gives it. The Debug provider writes it again,
    /// as <c>RPM.Desktop.Services.Cloud.CloudSyncService: Information: [Cloud] ...</c>. That second copy
    /// shows the type name again. Its format is fixed inside the framework, and both copies land in the
    /// same output window. So the Debug copy is the one that is removed.</para>
    ///
    /// <para>Call this after <c>UseLogging</c> in the builder chain. The provider must be registered before
    /// it can be removed.</para>
    /// </summary>
    public static IServiceCollection WithoutDebugLogProvider(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        for (int i = services.Count - 1; i >= 0; i--)
            if (services[i].ServiceType == typeof(ILoggerProvider)
                && services[i].ImplementationType == typeof(Microsoft.Extensions.Logging.Debug.DebugLoggerProvider))
                services.RemoveAt(i);

        return services;
    }
}
