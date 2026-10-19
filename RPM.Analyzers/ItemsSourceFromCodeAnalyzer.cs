using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace RPM.Analyzers;

/// <summary>
/// RPM008: a XAML control's ItemsSource set from code to a value whose static type is not a class.
///
/// <para>CsWinRT's AOT generator writes WinRT interfaces for the types it sees converted to a WinRT
/// value. At <c>list.ItemsSource = x</c> it sees x's static type. When that is a class, the vtable it
/// writes is the one the object needs. When it is an interface, the object's real type is whatever the
/// value happens to be, and if nothing else in the program names that type the object reaches WinRT
/// with no list interfaces at all. A collection expression typed IReadOnlyList&lt;T&gt; is the worst
/// case: its real type is one the compiler makes up and no line can ever name.</para>
///
/// <para>Covers plain assignments and object initializers. Matched on a property named ItemsSource,
/// typed object, on a type in Microsoft.UI.Xaml (both heads), so the app's own grid, which takes a typed
/// source, is not caught by name.</para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ItemsSourceFromCodeAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; }
        = ImmutableArray.Create(Rules.ItemsSourceFromCode);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(Inspect, OperationKind.SimpleAssignment);
    }

    private static void Inspect(OperationAnalysisContext context)
    {
        var assignment = (ISimpleAssignmentOperation)context.Operation;
        if (assignment.Target is not IPropertyReferenceOperation { Property: var property })
            return;
        if (property.Name != "ItemsSource" || property.Type.SpecialType != SpecialType.System_Object)
            return;
        if (!InXaml(property.ContainingType))
            return;

        var value = assignment.Value;
        while (value is IConversionOperation { IsImplicit: true } conversion)
            value = conversion.Operand;

        if (value.ConstantValue is { HasValue: true, Value: null })
            return;
        if (value.Type is not { } type)
            return;
        if (type is INamedTypeSymbol { TypeKind: TypeKind.Class, IsAnonymousType: false })
            return;

        context.ReportDiagnostic(Diagnostic.Create(
            Rules.ItemsSourceFromCode, assignment.Syntax.GetLocation(),
            type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
    }

    private static bool InXaml(INamedTypeSymbol? type)
    {
        for (var walk = type; walk is not null; walk = walk.BaseType)
        {
            var ns = walk.ContainingNamespace?.ToDisplayString() ?? "";
            if (ns == "Microsoft.UI.Xaml" || ns.StartsWith("Microsoft.UI.Xaml.", System.StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
