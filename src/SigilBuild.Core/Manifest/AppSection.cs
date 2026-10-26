// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

namespace SigilBuild.Core.Manifest;

/// <summary>The manifest's <c>app</c> section: the identity of the software being shipped.</summary>
/// <param name="Copyright">
/// <c>app.copyright</c>: the packed installer's LegalCopyright (Properties →
/// Details). Null when the manifest declares none, and the field is then left
/// empty. (R105)
/// </param>
public sealed record AppSection(
    string Id,
    string Name,
    string Version,
    string Publisher,
    string? Description,
    string? Homepage,
    string? Copyright = null);
