// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

namespace SigilBuild.Core.Manifest;

public readonly record struct SourceLocation(string File, int Line, int Column)
{
    public static readonly SourceLocation Unknown = new(string.Empty, 0, 0);

    public override string ToString() =>
        File.Length == 0 ? "<unknown>" : $"{File}:{Line}:{Column}";
}
