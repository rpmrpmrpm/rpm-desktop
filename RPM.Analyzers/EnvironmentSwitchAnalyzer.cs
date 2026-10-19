using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace RPM.Analyzers;

/// <summary>
/// RPM017: <c>Environment.GetEnvironmentVariable</c> called outside <c>DevSwitch</c>.
///
/// <para>A switch read directly works in the shipped app. <c>DevSwitch.Read</c> is the one place that
/// reads one, and it returns null in a Release build of the app. A constant name in
/// <see cref="SystemVariables"/> is allowed, because those are how the app finds tools and folders,
/// not switches. A name that is not a constant is reported, because nothing can tell what it
/// names.</para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class EnvironmentSwitchAnalyzer : DiagnosticAnalyzer
{
    private static readonly ImmutableHashSet<string> SystemVariables = ImmutableHashSet.Create(
        "PATH", "HOME", "TMPDIR", "USERPROFILE", "LOCALAPPDATA", "APPDATA",
        "XDG_CACHE_HOME", "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_RUNTIME_DIR");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; }
        = ImmutableArray.Create(Rules.EnvironmentSwitch);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(Inspect, OperationKind.Invocation);
    }

    private static void Inspect(OperationAnalysisContext context)
    {
        var call = (IInvocationOperation)context.Operation;
        var method = call.TargetMethod;
        if (method.Name != "GetEnvironmentVariable"
            || method.ContainingType is not { Name: "Environment" } owner
            || owner.ContainingNamespace?.ToDisplayString() != "System")
            return;

        if (context.ContainingSymbol.ContainingType is { Name: "DevSwitch" } within
            && within.ContainingNamespace?.ToDisplayString() == "RPM.Core.Diagnostics")
            return;

        var name = call.Arguments.Length > 0 ? call.Arguments[0].Value.ConstantValue : default;
        if (name.HasValue && name.Value is string constant && SystemVariables.Contains(constant))
            return;

        context.ReportDiagnostic(Diagnostic.Create(
            Rules.EnvironmentSwitch, call.Syntax.GetLocation(),
            name.HasValue && name.Value is string text ? $"\"{text}\"" : "a computed name"));
    }
}
