using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace RPM.Analyzers;

/// <summary>
/// RPM014: a line written with <c>Debug.Write*</c>, <c>Debug.Print</c> or <c>Trace.Write*</c>.
///
/// <para>The Debug calls compile to nothing in Release, which is every build that ships, and their
/// arguments are not evaluated either. The Trace calls reach only an attached debugger. Either way the
/// line is lost on a user's machine. <c>Said.Line</c> is the one exemption, because it is the place that
/// decides when the debug channel is still the right one (no ring, debugger attached).</para>
///
/// <para><c>Debug.Assert</c> and <c>Debug.Fail</c> are not lines and are not checked.</para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DebugOutputAnalyzer : DiagnosticAnalyzer
{
    private static readonly ImmutableHashSet<string> DebugMethods =
        ImmutableHashSet.Create("Write", "WriteLine", "WriteIf", "WriteLineIf", "Print");

    private static readonly ImmutableHashSet<string> TraceMethods =
        ImmutableHashSet.Create("Write", "WriteLine", "WriteIf", "WriteLineIf",
                                "TraceInformation", "TraceWarning", "TraceError");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; }
        = ImmutableArray.Create(Rules.DebugOutput);

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
        if (method.ContainingType is not { } owner
            || owner.ContainingNamespace?.ToDisplayString() != "System.Diagnostics")
            return;

        var reason = owner.Name switch
        {
            "Debug" when DebugMethods.Contains(method.Name) => "is removed from a Release build",
            "Trace" when TraceMethods.Contains(method.Name) => "reaches only an attached debugger",
            _ => null,
        };
        if (reason is null)
            return;

        if (context.ContainingSymbol.ContainingType is { Name: "Said" } within
            && within.ContainingNamespace?.ToDisplayString() == "RPM.Core.Diagnostics")
            return;

        context.ReportDiagnostic(Diagnostic.Create(
            Rules.DebugOutput, call.Syntax.GetLocation(), owner.Name, method.Name, reason));
    }
}
