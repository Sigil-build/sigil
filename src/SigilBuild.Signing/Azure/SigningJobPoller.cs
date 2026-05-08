using System;
using System.Threading;
using System.Threading.Tasks;

namespace SigilBuild.Signing.Azure;

public sealed class SigningJobPoller
{
    private readonly AzureTrustedSigningClient _client;
    private readonly TimeSpan _initialDelay;
    private readonly TimeSpan _maxDelay;
    private readonly TimeSpan _timeout;

    public SigningJobPoller(AzureTrustedSigningClient client,
        TimeSpan? initialDelay = null, TimeSpan? maxDelay = null, TimeSpan? timeout = null)
    {
        _client = client;
        _initialDelay = initialDelay ?? TimeSpan.FromMilliseconds(500);
        _maxDelay = maxDelay ?? TimeSpan.FromSeconds(5);
        _timeout = timeout ?? TimeSpan.FromMinutes(2);
    }

    public async Task<JobStatusResponse> WaitAsync(string jobId, CancellationToken ct)
    {
        var start = DateTimeOffset.UtcNow;
        var delay = _initialDelay;
        while (DateTimeOffset.UtcNow - start < _timeout)
        {
            var status = await _client.GetStatusAsync(jobId, ct);
            if (status.Status is "succeeded" or "failed") return status;
            await Task.Delay(delay, ct);
            delay = TimeSpan.FromMilliseconds(Math.Min(_maxDelay.TotalMilliseconds, delay.TotalMilliseconds * 2));
        }
        throw new TimeoutException($"Azure signing job {jobId} did not complete within {_timeout}");
    }
}
