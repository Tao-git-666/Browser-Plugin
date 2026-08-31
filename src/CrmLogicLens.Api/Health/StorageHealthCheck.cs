using CrmLogicLens.Api.Storage;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CrmLogicLens.Api.Health;

public sealed class StorageHealthCheck(IAnalysisStore store) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        await store.CheckWritableAsync(cancellationToken)
            ? HealthCheckResult.Healthy("The configured data directory is writable.")
            : HealthCheckResult.Unhealthy("The configured data directory is not writable.");
}
