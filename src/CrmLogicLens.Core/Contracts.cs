namespace CrmLogicLens.Core;

public enum ArtifactKind
{
    FormXml,
    JavaScript,
    RibbonXml,
    PluginCatalog,
    PluginAssembly,
    EntityMetadata,
    CustomPageCatalog,
    DecompiledCSharp
}

public enum EvidenceConfidence
{
    Confirmed,
    Inferred,
    Unknown
}

public sealed record CrmPageContext(
    string OrganizationUrl,
    string? OrganizationId,
    string? Version,
    string? ApiVersion,
    string PageType,
    string? EntityName,
    string? EntityId,
    string? FormId,
    string? AppId,
    string? FormLabel);

public sealed record ArtifactUpload(
    ArtifactKind Kind,
    string Name,
    string? ComponentId,
    string? Version,
    string MediaType,
    string ContentBase64,
    string? SourceUrl = null);

public sealed record SnapshotUpload(
    CrmPageContext Context,
    IReadOnlyList<ArtifactUpload> Artifacts,
    DateTimeOffset CapturedAt);

public sealed record SnapshotReceipt(
    Guid SnapshotId,
    Guid JobId,
    string Status,
    int ArtifactCount);

public sealed record EvidenceNode(
    string Id,
    string Kind,
    string Label,
    string Summary,
    string ArtifactName,
    string? Location,
    EvidenceConfidence Confidence,
    IReadOnlyDictionary<string, string>? Properties = null);

public sealed record EvidenceEdge(
    string SourceId,
    string TargetId,
    string Relation,
    EvidenceConfidence Confidence);

public sealed record EvidenceGraph(
    IReadOnlyList<EvidenceNode> Nodes,
    IReadOnlyList<EvidenceEdge> Edges,
    IReadOnlyList<string> Warnings);

public sealed record AnalysisResult(
    Guid SnapshotId,
    DateTimeOffset CompletedAt,
    EvidenceGraph Graph,
    string PlainLanguageSummary);

public sealed record ChatRequest(
    Guid SnapshotId,
    string Question,
    string? FocusComponentId = null,
    bool DataAccessConsent = false,
    IReadOnlyList<CrmDataQueryResult>? DataResults = null,
    IReadOnlyList<RuntimeDiagnosticEvidence>? RuntimeDiagnostics = null,
    IReadOnlyList<FormValueQueryResult>? FormValueResults = null,
    string? ContinuationId = null,
    bool RuntimeRecordingConsent = false,
    IReadOnlyList<RuntimeRecordingEvent>? RuntimeRecording = null);

public sealed record RuntimeDiagnosticEvidence(
    string Method,
    string Path,
    int Status,
    string? ResponseBody,
    DateTimeOffset CapturedAt);

public sealed record RuntimeRecordingEvent(
    int Sequence,
    string Kind,
    string Summary,
    string? Details,
    DateTimeOffset CapturedAt,
    string? Method = null,
    string? Path = null,
    int? Status = null);

public sealed record CrmDataQueryRequest(
    string RequestId,
    string Entity,
    IReadOnlyList<string> Select,
    string? Filter,
    string? OrderBy,
    int Top,
    string Purpose,
    bool CurrentRecord = false);

public sealed record CrmDataQueryResult(
    string RequestId,
    bool Success,
    string? Json = null,
    string? Error = null);

public sealed record FormValueQueryRequest(
    string RequestId,
    IReadOnlyList<string> Fields,
    string Purpose);

public sealed record FormValueQueryResult(
    string RequestId,
    bool Success,
    string? Json = null,
    string? Error = null);

public sealed record EvidenceCitation(
    string NodeId,
    string Label,
    string ArtifactName,
    string? Location,
    EvidenceConfidence Confidence);

public sealed record AnalysisTraceStep(
    int Sequence,
    string Title,
    string Summary,
    string? ToolName = null,
    string Status = "completed",
    long? DurationMs = null);

public sealed record ChatResponse(
    string Answer,
    EvidenceConfidence Confidence,
    IReadOnlyList<EvidenceCitation> Citations,
    IReadOnlyList<string> Unknowns,
    IReadOnlyList<AnalysisTraceStep>? Trace = null,
    IReadOnlyList<CrmDataQueryRequest>? DataRequests = null,
    IReadOnlyList<FormValueQueryRequest>? FormValueRequests = null,
    string? ContinuationId = null);

/// <summary>
/// An upload after bounded Base64 decoding. JavaScript, XML and JSON artifacts expose
/// <see cref="Text"/>; assemblies deliberately remain byte-only.
/// </summary>
public sealed record DecodedArtifact(
    ArtifactKind Kind,
    string Name,
    string? ComponentId,
    string? Version,
    string MediaType,
    ReadOnlyMemory<byte> Bytes,
    string? Text,
    string? SourceUrl);
