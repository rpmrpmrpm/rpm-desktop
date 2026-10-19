using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace RPM.Analyzers;

/// <summary>
/// RPM012: a SkiaSharp <c>PeekPixels()</c> result that is not declared with <c>using</c>.
///
/// <para>The pixmap owns a native <c>SkPixmap</c> and frees it in its finalizer. <c>SKPixmap.Encode</c>
/// and the other pixmap calls pass that native pixmap to Skia and keep no reference to themselves, and
/// Skia's encoder reads it by reference for every row. A pixmap that nothing references after the call
/// starts can be collected during it, and Skia then reads freed memory. A <c>using</c> declaration
/// keeps the pixmap alive to the end of its scope and frees it there.</para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PeekPixelsAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; }
        = ImmutableArray.Create(Rules.PeekPixelsWithoutUsing);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(Inspect, OperationKind.Invocation);
    }

    private static void Inspect(OperationAnalysisContext context)
    {
        var call = (IInvocationOperation)context.Operation;
        if (call.TargetMethod is not { Name: "PeekPixels", Parameters.Length: 0, ReturnType: { Name: "SKPixmap" } pixmap })
            return;
        if (pixmap.ContainingNamespace?.ToDisplayString() != "SkiaSharp")
            return;
        if (DeclaredWithUsing(call))
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rules.PeekPixelsWithoutUsing, call.Syntax.GetLocation()));
    }

    /// <summary>Whether the call is the initializer of a variable in a <c>using</c> declaration
    /// (<c>using var p = x.PeekPixels();</c>) or a <c>using</c> statement
    /// (<c>using (var p = x.PeekPixels())</c>).</summary>
    private static bool DeclaredWithUsing(IOperation call)
    {
        var parent = call.Parent;
        while (parent is IConversionOperation { IsImplicit: true })
            parent = parent.Parent;

        return parent is IVariableInitializerOperation
        {
            Parent: IVariableDeclaratorOperation
            {
                Parent: IVariableDeclarationOperation
                {
                    Parent: IVariableDeclarationGroupOperation
                    {
                        Parent: IUsingDeclarationOperation or IUsingOperation,
                    },
                },
            },
        };
    }
}
