using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace RPM.Analyzers;

/// <summary>
/// RPM010: a call to <c>XamlReader.Load</c>.
///
/// <para>Markup in a string is parsed at run time, out of sight of every generator that makes the
/// Native AOT build work, and a template built that way can throw under AOT. Markup in a .xaml file is
/// compiled with the rest. No caller is allowed.</para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class XamlReaderAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; }
        = ImmutableArray.Create(Rules.XamlReaderLoad);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(Inspect, OperationKind.Invocation);
    }

    private static void Inspect(OperationAnalysisContext context)
    {
        var call = (IInvocationOperation)context.Operation;
        if (call.TargetMethod is not { Name: "Load", ContainingType: { Name: "XamlReader" } owner })
            return;
        if (owner.ContainingNamespace?.ToDisplayString() != "Microsoft.UI.Xaml.Markup")
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rules.XamlReaderLoad, call.Syntax.GetLocation()));
    }
}
