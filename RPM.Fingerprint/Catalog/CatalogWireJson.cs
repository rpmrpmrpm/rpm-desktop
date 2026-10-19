using System.Text.Json.Serialization;

namespace RPM.Fingerprint.Catalog;

/// <summary>
/// The shapes the rpm API answers with, described for the System.Text.Json source generator, with the
/// naming the API actually uses.
///
/// <para>The server is an ASP.NET Core controller API with default options, so every property on the
/// wire is camelCase: <c>{"results":[…]}</c>, not <c>{"Results":[…]}</c>. The contracts next door are
/// spelled PascalCase and carry no <see cref="JsonPropertyNameAttribute"/>, so the naming policy here
/// is the only thing that connects the two. Get it wrong and nothing throws: every property comes back
/// null, a required list included, and the first thing to enumerate one gets a
/// NullReferenceException.</para>
///
/// <para>That is why this is one context in the contracts' own project rather than a per-client one.
/// A client that reads these types uses this, and the rule about the wire is stated once, next to the
/// types it is a rule about.</para>
/// </summary>
/// <remarks>A generated context defaults to neither camelCase names nor case-insensitive matching.
/// Both are set here, as <see cref="System.Text.Json.JsonSerializerDefaults.Web"/> sets them.</remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<CatalogRelease>))]
[JsonSerializable(typeof(CatalogReleaseDetail))]
[JsonSerializable(typeof(CatalogPapers))]
[JsonSerializable(typeof(List<CatalogArtist>))]
[JsonSerializable(typeof(CatalogArtist))]
[JsonSerializable(typeof(CatalogPlays))]
[JsonSerializable(typeof(CatalogDisc))]
[JsonSerializable(typeof(CatalogCover))]
[JsonSerializable(typeof(CoverSearchResponse))]
public sealed partial class CatalogWireJson : JsonSerializerContext;
