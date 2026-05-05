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
