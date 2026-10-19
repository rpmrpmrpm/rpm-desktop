// The attribute that the app's view rows use, so that a classic {Binding} can read them. Both
// platform heads give it the same name. One alias is used instead of a #if at each of the
// seventy-seven declarations.
//
// SKIA. It is Microsoft.UI.Xaml.Data.BindableAttribute. Uno's bindable-metadata generator reads it and
// writes a table of the type's members. That table is how {Binding} finds a property by name, without
// reflection.
//
// WINUI. It is WinRT.GeneratedBindableCustomPropertyAttribute, which does the same job with a different
// generator. CsWinRT's WinRTAotSourceGenerator writes the type an IBindableCustomPropertyImplementation
// whose accessors are static lambdas over the property itself:
//
//     static (instance) => ((ReleaseLookupRow)instance).Detail
//
// Why not [Bindable] here: WinUI's markup compiler reads that attribute and writes XamlTypeInfo.g.cs.
// That generator does not handle init-only properties or required members. It emits `that.Prop = value;`
// for every settable-looking property, and `new T()` for every type with a public parameterless
// constructor. Many of these rows have init-only properties, and some of the types they refer to have
// required members. So the generated file does not compile, and the errors are in generated code, not in
// code anyone wrote. CsWinRT's generator reads CanWrite and emits a null setter for an init-only
// property instead of assigning to it. It never constructs the type, so neither problem occurs.
//
// Why this exists: under WinUI on CoreCLR, a {Binding} path is resolved by reflection when the type is
// not registered. Under NativeAOT the trimmer removes the members that nothing references in code. A
// property named by a string in XAML is not such a reference, so the reflection fallback then fails. It
// fails silently: the binding does not resolve, the target keeps its default value, and the symptom is an
// empty cell, not an exception. The static lambdas above are references, so the trimmer keeps the members.
//
// EVERY TYPE THAT WEARS THIS MUST BE partial. The generator adds a partial declaration to the type. A type
// that is not partial is skipped silently: no error and no warning. It then falls back to the reflection
// path that this attribute exists to avoid. BindableRowsTests guards this.

#if !RPM_WINUI
global using RpmBindableAttribute = Microsoft.UI.Xaml.Data.BindableAttribute;
#else
global using RpmBindableAttribute = WinRT.GeneratedBindableCustomPropertyAttribute;
#endif
