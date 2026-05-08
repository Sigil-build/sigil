using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace SigilBuild.Cli.Tests;

[CollectionDefinition("CliCommands", DisableParallelization = true)]
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "xUnit requires the marker class to be named *Collection by convention.")]
public sealed class CliCommandsCollection { }
