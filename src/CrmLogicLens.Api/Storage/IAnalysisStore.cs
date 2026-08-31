using CrmLogicLens.Core;

namespace CrmLogicLens.Api.Storage;

public interface IAnalysisStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<StoredArtifact> StoreArtifactAsync(PreparedArtifact artifact, CancellationToken cancellationToken = default);

    Task SaveSnapshotAsync(StoredSnapshot snapshot, CancellationToken cancellationToken = default);

    Task<StoredSnapshot?> GetSnapshotAsync(Guid snapshotId, CancellationToken cancellationToken = default);

    Task<SnapshotUpload?> LoadSnapshotUploadAsync(Guid snapshotId, CancellationToken cancellationToken = default);

    Task<ArtifactUpload?> LoadArtifactAsync(
        Guid snapshotId,
        ArtifactKind kind,
        string name,
        CancellationToken cancellationToken = default);

    Task SaveJobAsync(AnalysisJob job, CancellationToken cancellationToken = default);

    Task<AnalysisJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AnalysisJob>> GetRecoverableJobsAsync(CancellationToken cancellationToken = default);

    Task SaveAnalysisAsync(AnalysisResult result, CancellationToken cancellationToken = default);

    Task<AnalysisResult?> GetAnalysisAsync(Guid snapshotId, CancellationToken cancellationToken = default);

    Task<bool> CheckWritableAsync(CancellationToken cancellationToken = default);
}
