using CrmLogicLens.Api.Storage;
using CrmLogicLens.Core;

namespace CrmLogicLens.Api.Services;

public sealed class AnalysisWorker(
    AnalysisJobQueue queue,
    IAnalysisStore store,
    DecompilerProcessService decompiler,
    AnalysisPipeline pipeline,
    TimeProvider timeProvider,
    ILogger<AnalysisWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await store.InitializeAsync(stoppingToken);

        // Recovery runs as a producer so a backlog larger than the bounded channel cannot
        // deadlock application startup before the consumer begins reading.
        var recovery = RecoverPersistedJobsAsync(stoppingToken);

        try
        {
            await foreach (var jobId in queue.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await ProcessJobAsync(jobId, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Analysis worker could not process job {JobId}", jobId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            try
            {
                await recovery;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Persisted analysis jobs could not be recovered");
            }
        }
    }

    private async Task RecoverPersistedJobsAsync(CancellationToken cancellationToken)
    {
        var recoverableJobs = await store.GetRecoverableJobsAsync(cancellationToken);
        if (recoverableJobs.Count > 0)
        {
            logger.LogInformation("Requeueing {JobCount} persisted analysis jobs", recoverableJobs.Count);
        }

        foreach (var job in recoverableJobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pending = job with
            {
                Status = AnalysisJobStatus.Pending,
                UpdatedAt = timeProvider.GetUtcNow(),
                StartedAt = null,
                CompletedAt = null,
                ErrorCode = null,
                ErrorMessage = null
            };
            await store.SaveJobAsync(pending, cancellationToken);
            await queue.EnqueueAsync(job.JobId, cancellationToken);
        }
    }

    private async Task ProcessJobAsync(Guid jobId, CancellationToken stoppingToken)
    {
        var job = await store.GetJobAsync(jobId, stoppingToken);
        if (job is null || job.Status is AnalysisJobStatus.Completed or AnalysisJobStatus.Failed)
        {
            return;
        }

        var startedAt = timeProvider.GetUtcNow();
        var running = job with
        {
            Status = AnalysisJobStatus.Running,
            Attempts = job.Attempts + 1,
            UpdatedAt = startedAt,
            StartedAt = startedAt,
            CompletedAt = null,
            ErrorCode = null,
            ErrorMessage = null
        };
        await store.SaveJobAsync(running, stoppingToken);

        try
        {
            var upload = await store.LoadSnapshotUploadAsync(job.SnapshotId, stoppingToken)
                ?? throw new InvalidDataException("The snapshot for this job does not exist.");
            var enrichment = decompiler.EagerAnalysisEnabled
                ? await decompiler.EnrichAsync(upload, stoppingToken)
                : new DecompilerEnrichment(upload, []);
            if (decompiler.EagerAnalysisEnabled)
            {
                await PersistDerivedArtifactsAsync(
                    job.SnapshotId,
                    upload,
                    enrichment.Upload,
                    stoppingToken);
            }

            var result = await Task.Run(
                () => pipeline.Analyze(enrichment.Upload, stoppingToken, job.SnapshotId),
                stoppingToken);
            if (enrichment.Warnings.Count > 0)
            {
                result = result with
                {
                    PlainLanguageSummary = $"{result.PlainLanguageSummary} " +
                        $"反编译隔离阶段另有 {enrichment.Warnings.Count} 项告警，详见 warnings。",
                    Graph = result.Graph with
                    {
                        Warnings = result.Graph.Warnings
                            .Concat(enrichment.Warnings)
                            .Distinct(StringComparer.Ordinal)
                            .ToArray()
                    }
                };
            }

            await store.SaveAnalysisAsync(result, stoppingToken);

            var completedAt = timeProvider.GetUtcNow();
            await store.SaveJobAsync(running with
            {
                Status = AnalysisJobStatus.Completed,
                UpdatedAt = completedAt,
                CompletedAt = completedAt
            }, stoppingToken);

            logger.LogInformation(
                "Analysis job {JobId} completed for snapshot {SnapshotId}",
                job.JobId,
                job.SnapshotId);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            await TryReturnToPendingAsync(running);
        }
        catch (Exception exception)
        {
            var failedAt = timeProvider.GetUtcNow();
            var errorId = Guid.NewGuid().ToString("N");
            await store.SaveJobAsync(running with
            {
                Status = AnalysisJobStatus.Failed,
                UpdatedAt = failedAt,
                CompletedAt = failedAt,
                ErrorCode = "analysis_failed",
                ErrorMessage = $"Analysis failed. Consult server logs with error identifier {errorId}."
            }, CancellationToken.None);

            logger.LogError(
                exception,
                "Analysis job {JobId} failed for snapshot {SnapshotId}; error identifier {ErrorId}",
                job.JobId,
                job.SnapshotId,
                errorId);
        }
    }

    private async Task PersistDerivedArtifactsAsync(
        Guid snapshotId,
        SnapshotUpload original,
        SnapshotUpload enriched,
        CancellationToken cancellationToken)
    {
        var additions = enriched.Artifacts
            .Where(artifact => artifact.Kind == ArtifactKind.DecompiledCSharp)
            .Where(artifact => !original.Artifacts.Any(existing =>
                existing.Kind == artifact.Kind &&
                string.Equals(existing.Name, artifact.Name, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (additions.Length == 0)
        {
            return;
        }

        var snapshot = await store.GetSnapshotAsync(snapshotId, cancellationToken)
            ?? throw new InvalidDataException("The snapshot disappeared while storing decompiled evidence.");
        var storedArtifacts = snapshot.Artifacts.ToList();
        foreach (var artifact in additions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] content;
            try
            {
                content = Convert.FromBase64String(artifact.ContentBase64);
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException("The isolated decompiler returned invalid encoded source.", exception);
            }

            if (content.Length == 0)
            {
                throw new InvalidDataException("The isolated decompiler returned empty source.");
            }

            var stored = await store.StoreArtifactAsync(new PreparedArtifact(
                artifact.Kind,
                artifact.Name,
                artifact.ComponentId,
                artifact.Version,
                artifact.MediaType,
                content,
                artifact.SourceUrl), cancellationToken);
            storedArtifacts.Add(stored);
        }

        await store.SaveSnapshotAsync(snapshot with { Artifacts = storedArtifacts }, cancellationToken);
        logger.LogInformation(
            "Stored {ArtifactCount} decompiled source artifact(s) for snapshot {SnapshotId}",
            additions.Length,
            snapshotId);
    }

    private async Task TryReturnToPendingAsync(AnalysisJob running)
    {
        try
        {
            await store.SaveJobAsync(running with
            {
                Status = AnalysisJobStatus.Pending,
                UpdatedAt = timeProvider.GetUtcNow(),
                StartedAt = null
            }, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not return analysis job {JobId} to pending state", running.JobId);
        }
    }
}
