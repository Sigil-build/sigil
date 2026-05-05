using System.Threading;
using System.Threading.Tasks;
using SigilBuild.Core.Manifest;

namespace SigilBuild.Packaging;

public interface IPackager
{
    PackageFormat Format { get; }
    Task<PackResult> PackAsync(SigilManifest manifest, PackOptions options, CancellationToken ct);
}
