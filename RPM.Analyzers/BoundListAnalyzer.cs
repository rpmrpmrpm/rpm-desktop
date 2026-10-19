using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RPM.Analyzers;

/// <summary>
/// RPM007: a property that a XAML <c>ItemsSource="{Binding X}"</c> names, typed as something a Native
/// AOT build hands to the control empty.
///
/// RPM011: a property that a XAML binding indexes into (<c>{Binding X[3].Gain}</c>), typed as something
/// CsWinRT gives no indexer to. WinUI head only.
///
/// <para>The XAML is read from the project's AdditionalFiles. Both heads already pass every page there
/// (Uno's generator and WinUI's both read them), so this needs nothing in the project file.</para>
///
/// <para>Matched by property name, not by the template's x:DataType, because half the bindings have no
/// x:DataType (their DataContext is set from code). Only types marked [RpmBindable] are checked: a
/// {Binding} cannot read any other type under AOT, so a same-named property elsewhere is not bound.
/// Only collection types are checked, so a bound name that is also, say, a bool elsewhere is left
/// alone.</para>
///
/// <para>RPM007 allows only a non-generic partial class declared in this project, which CsWinRT always
/// generates for. A generic type is generated only when some line converts that exact instantiation to
/// a WinRT value. For example, <c>List&lt;AlbumTrackItem&gt;</c> works only because the album page sets
/// one from code, and <c>List&lt;TrackInfoRow&gt;</c>, which nothing sets, arrives empty. RPM011 allows
/// only a partial class of this project marked [RpmBindable].</para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BoundListAnalyzer : DiagnosticAnalyzer
{
    // ItemsSource="{Binding Cards, FallbackValue=...}", "{Binding Path=Model.Rows}": the last path segment.
    private static readonly Regex ItemsSourceBinding = new(
        @"ItemsSource\s*=\s*""\{Binding\s+(?:Path\s*=\s*)?([\w.]+)", RegexOptions.Compiled);

    // "{Binding Bands[3].Gain}": the segment the index is applied to.
    private static readonly Regex IndexedBinding = new(
        @"\{Binding\s+(?:Path\s*=\s*)?([\w.]+)\[", RegexOptions.Compiled);

    // The WinUI head's [RpmBindable]. Present in the compilation only on that head.
    private const string WinUiBindable = "WinRT.GeneratedBindableCustomPropertyAttribute";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; }
        = ImmutableArray.Create(Rules.BoundList, Rules.IndexedBinding);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var (bound, indexed) = Names(start.Options.AdditionalFiles, start.CancellationToken);
            if (bound.Count == 0 && indexed.Count == 0)
                return;

            var facts = new Facts(
                start.Compilation,
                start.Compilation.GetSpecialType(SpecialType.System_Collections_IEnumerable),
                start.Compilation.GetTypeByMetadataName(WinUiBindable));

            start.RegisterSymbolAction(
                symbol => Inspect(symbol, bound, indexed, facts), SymbolKind.Property);
        });
    }

    private sealed class Facts(
        Compilation compilation, INamedTypeSymbol enumerable, INamedTypeSymbol? winUiBindable)
    {
        public Compilation Compilation { get; } = compilation;
        public INamedTypeSymbol Enumerable { get; } = enumerable;
        public INamedTypeSymbol? WinUiBindable { get; } = winUiBindable;
    }

    private static (HashSet<string> Bound, HashSet<string> Indexed) Names(
        ImmutableArray<AdditionalText> files, System.Threading.CancellationToken ct)
    {
        var bound = new HashSet<string>();
        var indexed = new HashSet<string>();
        foreach (var file in files)
        {
            if (!file.Path.EndsWith(".xaml", System.StringComparison.OrdinalIgnoreCase))
                continue;
            if (file.GetText(ct)?.ToString() is not { } text)
                continue;
            foreach (Match match in ItemsSourceBinding.Matches(text))
                bound.Add(LastSegment(match.Groups[1].Value));
            foreach (Match match in IndexedBinding.Matches(text))
                indexed.Add(LastSegment(match.Groups[1].Value));
        }
        return (bound, indexed);
    }

    private static string LastSegment(string path) => path.Substring(path.LastIndexOf('.') + 1);

    private static void Inspect(
        SymbolAnalysisContext context, HashSet<string> bound, HashSet<string> indexed, Facts facts)
    {
        var property = (IPropertySymbol)context.Symbol;
        if (property.IsStatic || property.IsIndexer || property.DeclaredAccessibility != Accessibility.Public)
            return;

        var isBound = bound.Contains(property.Name);
        var isIndexed = indexed.Contains(property.Name) && facts.WinUiBindable is not null;
        if (!isBound && !isIndexed)
            return;
        if (!IsBindable(property.ContainingType))
            return;

        var type = property.Type;
        if (type.SpecialType is SpecialType.System_String or SpecialType.System_Object)
            return;
        if (!IsCollection(type, facts.Enumerable))
            return;

        var at = property.Locations.FirstOrDefault(l => l.IsInSource) ?? Location.None;
        var name = $"{property.ContainingType.Name}.{property.Name}";
        var shown = type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

        if (isBound && !AllowedAsList(type, facts))
            context.ReportDiagnostic(Diagnostic.Create(Rules.BoundList, at, name, shown));

        if (isIndexed && !AllowedIndexed(type, facts))
            context.ReportDiagnostic(Diagnostic.Create(Rules.IndexedBinding, at, name, property.Name, shown));
    }

    // [RpmBindable] is an alias: BindableAttribute on the Skia head, GeneratedBindableCustomPropertyAttribute
    // on the WinUI head.
    private static bool IsBindable(INamedTypeSymbol type) => type.GetAttributes().Any(a =>
        a.AttributeClass?.Name is "BindableAttribute" or "GeneratedBindableCustomPropertyAttribute");

    private static bool IsCollection(ITypeSymbol type, INamedTypeSymbol enumerable) =>
        type is IArrayTypeSymbol
        || SymbolEqualityComparer.Default.Equals(type, enumerable)
        || type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, enumerable));

    private static bool AllowedAsList(ITypeSymbol type, Facts facts) =>
        type is INamedTypeSymbol { TypeKind: TypeKind.Class } named
        && IsOwnPartialClass(named, facts.Compilation);

    private static bool AllowedIndexed(ITypeSymbol type, Facts facts) =>
        type is INamedTypeSymbol { TypeKind: TypeKind.Class } named
        && IsOwnPartialClass(named, facts.Compilation)
        && named.GetAttributes().Any(a =>
            SymbolEqualityComparer.Default.Equals(a.AttributeClass, facts.WinUiBindable));

    private static bool IsOwnPartialClass(INamedTypeSymbol named, Compilation compilation) =>
        !named.IsGenericType
        && SymbolEqualityComparer.Default.Equals(named.ContainingAssembly, compilation.Assembly)
        && named.DeclaringSyntaxReferences.Any(r =>
            r.GetSyntax() is TypeDeclarationSyntax d && d.Modifiers.Any(SyntaxKind.PartialKeyword));
}
