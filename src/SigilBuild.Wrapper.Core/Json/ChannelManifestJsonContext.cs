using System.Text.Json;
using System.Text.Json.Serialization;
using SigilBuild.Wrapper.Update;

namespace SigilBuild.Wrapper.Json;

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> used to parse the
/// signed channel manifest fetched from <c>updates.manifestUrl</c> at
/// <c>/Update</c> time (P12, T12.1), without any reflection — required
/// because the wrapper runtime is published Native AOT with
/// <c>TrimMode=full</c>. Mirrors <see cref="WrapperBlobJsonContext"/>'s
/// options exactly.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ChannelManifest))]
internal partial class ChannelManifestJsonContext : JsonSerializerContext
{
}
