using System.Text.Json;
using System.Text.Json.Serialization;
using SigilBuild.Core.Manifest;

namespace SigilBuild.Wrapper.Json;

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> used to serialize
/// and deserialize the embedded <c>SIGIL_BLOB_V1</c> resource without any
/// reflection — required because the wrapper runtime is published Native
/// AOT with <c>TrimMode=full</c>.
/// </summary>
/// <remarks>
/// The context covers the wire-format DTOs in
/// <see cref="SerializableWrapperBlob"/> /
/// <see cref="SerializableInstallStep"/> /
/// <see cref="SerializableParameterDefinition"/>. Neither
/// <see cref="InstallStep"/> nor <see cref="ParameterDefinition"/> is
/// declared here — both contain shapes that AOT JSON cannot generate
/// without extra ceremony (a discriminator setup for the install-step
/// hierarchy and an <c>object?</c> default for parameters). The flat
/// DTOs above provide stable wire schemas instead.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(SerializableWrapperBlob))]
[JsonSerializable(typeof(SerializableInstallStep))]
[JsonSerializable(typeof(SerializableInstallStep[]))]
[JsonSerializable(typeof(SerializableParameterDefinition))]
[JsonSerializable(typeof(SerializableParameterDefinition[]))]
[JsonSerializable(typeof(ParameterType))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(int[]))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(bool))]
internal partial class WrapperBlobJsonContext : JsonSerializerContext
{
}
