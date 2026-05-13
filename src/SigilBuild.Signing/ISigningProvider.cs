using System.Threading;
using System.Threading.Tasks;

namespace SigilBuild.Signing;

public interface ISigningProvider
{
    string Name { get; }
    Task<SignResult> SignAsync(SignOptions options, CancellationToken ct);
}
