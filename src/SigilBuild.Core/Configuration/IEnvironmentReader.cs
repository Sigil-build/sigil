// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using System.Diagnostics.CodeAnalysis;

namespace SigilBuild.Core.Configuration;

[SuppressMessage("Naming", "CA1716:Identifiers should not match keywords",
    Justification = "Plan-specified API; not consumed from VB.")]
public interface IEnvironmentReader
{
    string? Get(string name);
}

public sealed class ProcessEnvironmentReader : IEnvironmentReader
{
    public string? Get(string name) => System.Environment.GetEnvironmentVariable(name);
}
