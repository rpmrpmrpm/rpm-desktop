using System.Text.Json.Serialization;

namespace RPM.Fingerprint.Catalog;

/// <summary>The body ListenBrainz is asked the listening figures with.</summary>
/// <remarks>A named type rather than an anonymous one because the source generator below cannot
/// describe an anonymous type, and reflection-based serialization is off in a published build.
/// The property names are the service's, so they are spelled out rather than left to a naming
/// policy.</remarks>
internal sealed record PlaysRequest(
    [property: JsonPropertyName("recording_mbids")] IReadOnlyList<string> RecordingMbids);

/// <summary>One recording in the body the canonical-mapping service is asked with.</summary>
internal sealed record CanonicalRequestRow(
    [property: JsonPropertyName("recording_mbid")] string RecordingMbid);

/// <summary>
/// The serializable shapes this project writes, listed for the System.Text.Json source generator.
/// </summary>
/// <remarks>
/// Every type written by <see cref="TrackPopularity"/> is named here. A published build is Native AOT
/// and sets <c>IsReflectionEnabledByDefault=false</c>, so a type that is not on this list throws
/// InvalidOperationException at the call rather than serializing. Reads are not listed because they go
/// through JsonDocument, which needs no metadata.
/// </remarks>
[JsonSerializable(typeof(PlaysRequest))]
[JsonSerializable(typeof(IReadOnlyList<CanonicalRequestRow>))]
internal sealed partial class CatalogJson : JsonSerializerContext;
