// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

namespace SigilBuild.Wrapper.Codec;

/// <summary>
/// One file in a payload container: its container-relative path (forward-slash
/// separated) and raw, uncompressed bytes. The <see cref="PayloadCodec"/> sorts
/// entries by <see cref="RelativePath"/> for deterministic output, so callers may
/// supply them in any order.
/// </summary>
internal readonly record struct PayloadEntry(string RelativePath, byte[] Content);
