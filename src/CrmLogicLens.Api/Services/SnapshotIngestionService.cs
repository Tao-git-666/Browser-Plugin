using CrmLogicLens.Api.Storage;
using CrmLogicLens.Api.Validation;
using CrmLogicLens.Core;

namespace CrmLogicLens.Api.Services;

public sealed class SnapshotIngestionService(
    SnapshotUploadValidator validator,
    IAnalysisStore store,
    AnalysisJobQueue queue,
    TimeProvider timeProvider)
{
    public async Task<SnapshotReceipt> IngestAsync(
        SnapshotUpload? upload,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var preparedArtifacts = validator.ValidateAndPrepare(upload, now);
        var snapshotId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        var storedArtifacts = new List<StoredArtifact>(preparedArtifacts.Count);
        foreach (var artifact in preparedArtifacts)
        {
            storedArtifacts.Add(await store.StoreArtifactAsync(artifact, cancellationToken));
        }

        var snapshot = new StoredSnapshot(
            snapshotId,
            jobId,
            upload!.Context,
            storedArtifacts,
            upload.CapturedAt,
            now);
        var job = new AnalysisJob(
            jobId,
            snapshotId,
            AnalysisJobStatus.Pending,
            Attempts: 0,
            CreatedAt: now,
            UpdatedAt: now);

        await store.SaveSnapshotAsync(snapshot, cancellationToken);
        await store.SaveJobAsync(job, cancellationToken);

        // Persistence is already committed. Do not abandon this durable job merely because
        // the HTTP client disconnected while bounded-queue backpressure was being applied.
        await queue.EnqueueAsync(jobId, CancellationToken.None);

        return new SnapshotReceipt(snapshotId, jobId, AnalysisJobStatus.Pending, storedArtifacts.Count);
    }
}
