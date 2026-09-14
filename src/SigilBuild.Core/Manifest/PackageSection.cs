// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using System.Collections.Generic;

namespace SigilBuild.Core.Manifest;

public enum PackageFormat { Msix, Zip, Exe }
public enum TargetArchitecture { X64, Arm64 }

public sealed record MsixOptions(
    string? Publisher,
    string? Logo,
    IReadOnlyList<string>? Capabilities,
    bool RunWack = false);

public sealed record PackageSection(
    IReadOnlyList<PackageFormat> Formats,
    IReadOnlyList<TargetArchitecture> Architectures,
    MsixOptions? Msix);
