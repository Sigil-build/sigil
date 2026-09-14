// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace SigilBuild.Cli.Tests;

[CollectionDefinition("CliCommands", DisableParallelization = true)]
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "xUnit requires the marker class to be named *Collection by convention.")]
public sealed class CliCommandsCollection { }
