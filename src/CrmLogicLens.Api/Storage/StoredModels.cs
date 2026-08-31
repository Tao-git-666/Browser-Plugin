using CrmLogicLens.Core;

namespace CrmLogicLens.Api.Storage;

public sealed record StoredArtifact(
    ArtifactKind Kind,
    string Name,
    string? ComponentId,
    string? Version,
    string MediaType,
    string Sha256,
    long SizeBytes,
    string? SourceUrl);

public sealed record StoredSnapshot(
    Guid SnapshotId,
    Guid JobId,
    CrmPageContext Context,
    IReadOnlyList<StoredArtifact> Artifacts,
    DateTimeOffset CapturedAt,
    DateTimeOffset ReceivedAt);

public static class AnalysisJobStatus
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

public sealed record AnalysisJob(
    Guid JobId,
    Guid SnapshotId,
    string Status,
    int Attempts,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? CompletedAt = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public sealed record PreparedArtifact(
    ArtifactKind Kind,
    string Name,
    string? ComponentId,
    string? Version,
    string MediaType,
    byte[] Content,
    string? SourceUrl);
