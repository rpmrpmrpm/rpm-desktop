using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace RPM.Analyzers;

/// <summary>
/// RPM018: an ONNX Runtime <c>InferenceSession</c> built outside <c>OnnxSession</c>.
///
/// <para>The runtime's constructor does not keep the <c>SessionOptions</c> alive while the native
/// <c>CreateSession</c> call reads them. A caller that keeps no reference either (the usual
/// <c>new InferenceSession(path, new SessionOptions { ... })</c>) can have them finalised during the
/// call, and the process crashes. <c>OnnxSession.Open</c> holds them until the session exists.</para>
///
/// <para>Matched on the declaring type rather than on a file name, so moving code to another file does
/// not change what is allowed.</para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HandBuiltOnnxSessionAnalyzer : DiagnosticAnalyzer
{
    private const string Allowed = "OnnxSession";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; }
        = ImmutableArray.Create(Rules.HandBuiltOnnxSession);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(Inspect, OperationKind.ObjectCreation);
    }

    private static void Inspect(OperationAnalysisContext context)
    {
        var created = (IObjectCreationOperation)context.Operation;
        if (created.Type is not { Name: "InferenceSession" } type
            || type.ContainingNamespace?.ToDisplayString() != "Microsoft.ML.OnnxRuntime")
            return;

        for (var owner = context.ContainingSymbol.ContainingType; owner is not null; owner = owner.ContainingType)
            if (owner.Name == Allowed)
                return;

        context.ReportDiagnostic(Diagnostic.Create(Rules.HandBuiltOnnxSession, created.Syntax.GetLocation()));
    }
}
