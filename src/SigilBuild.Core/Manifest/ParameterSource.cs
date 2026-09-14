// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

namespace SigilBuild.Core.Manifest;

/// <summary>
/// Dynamic options source for an enum parameter. The wizard fetches the URL
/// (substituting ${parameters.*} placeholders) when the user navigates to
/// Install Options, parses the JSON, and populates the parameter's ComboBox
/// with (label, value) pairs.
/// </summary>
public sealed record ParameterSource(
    string Url,
    string ItemsPath,        // top-level JSON property name, e.g. "data"
    string ValueProperty,    // e.g. "applicationId"
    string LabelProperty);   // e.g. "applicationName"
