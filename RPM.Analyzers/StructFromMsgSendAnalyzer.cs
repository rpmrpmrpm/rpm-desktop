using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RPM.Analyzers;

/// <summary>
/// RPM013: a <c>DllImport</c> or <c>LibraryImport</c> of <c>objc_msgSend</c> (or any
/// <c>objc_msgSend*</c> entry point) whose return type is a struct, anywhere but
/// <c>RPM.Desktop.Services.Platform.ObjcStructReturn</c>.
///
/// <para>On x86_64 a struct larger than 16 bytes is returned through a buffer passed as a hidden first
/// argument, and the message must go through <c>objc_msgSend_stret</c>; plain <c>objc_msgSend</c> takes
/// the buffer for the receiver and crashes. On arm64 there is no <c>objc_msgSend_stret</c>. A P/Invoke
/// binds the entry point it names, so a single declaration is wrong on one of the two. Every struct
/// return goes through the one type that picks by architecture, including the 16-byte ones that would
/// be safe, so that the size rule is decided in one place.</para>
///
/// <para>Primitives, pointers and enums are not structs here: <c>nint</c>, <c>double</c> and
/// <c>bool</c> come back in registers on both architectures.</para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class StructFromMsgSendAnalyzer : DiagnosticAnalyzer
{
    private const string Allowed = "RPM.Desktop.Services.Platform.ObjcStructReturn";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; }
        = ImmutableArray.Create(Rules.StructFromMsgSend);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(Inspect, SymbolKind.Method);
    }

    private static void Inspect(SymbolAnalysisContext context)
    {
        var method = (IMethodSymbol)context.Symbol;
        if (method.ReturnType is not { TypeKind: TypeKind.Struct, SpecialType: SpecialType.None } returned)
            return;

        var entry = EntryPoint(method);
        if (entry is null || !entry.StartsWith("objc_msgSend", System.StringComparison.Ordinal))
            return;
        if (method.ContainingType?.ToDisplayString() == Allowed)
            return;

        context.ReportDiagnostic(Diagnostic.Create(
            Rules.StructFromMsgSend, method.Locations[0], method.Name, entry, returned.Name));
    }

    /// <summary>The native function the method binds to, or null when it is not an import.</summary>
    private static string? EntryPoint(IMethodSymbol method)
    {
        if (method.GetDllImportData() is { } dll)
            return dll.EntryPointName ?? method.Name;

        foreach (var attribute in method.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() != "System.Runtime.InteropServices.LibraryImportAttribute")
                continue;
            foreach (var named in attribute.NamedArguments)
                if (named.Key == "EntryPoint" && named.Value.Value is string name)
                    return name;
            return method.Name;
        }

        return null;
    }
}
