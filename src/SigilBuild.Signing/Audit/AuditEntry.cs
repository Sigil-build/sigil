// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using System;
using System.Text.Json.Serialization;

namespace SigilBuild.Signing.Audit;

public sealed record AuditEntry(
    [property: JsonPropertyName("timestamp")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("artifact")] string Artifact,
    [property: JsonPropertyName("file_hash")] string FileHash,
    [property: JsonPropertyName("thumbprint")] string? Thumbprint,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("message")] string? Message);

[JsonSerializable(typeof(AuditEntry))]
internal sealed partial class AuditEntryJsonContext : JsonSerializerContext { }
