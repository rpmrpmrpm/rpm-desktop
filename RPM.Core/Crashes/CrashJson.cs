using System.Text.Json.Serialization;

namespace RPM.Core.Crashes;

/// <summary>The crash files' JSON. Source-generated because the Mac build is Native AOT.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(CrashFile))]
[JsonSerializable(typeof(PendingCrash))]
internal sealed partial class CrashJson : JsonSerializerContext;
