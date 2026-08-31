using System.Text.Json;
using CrmLogicLens.Api.Configuration;
using CrmLogicLens.Api.Services;
using CrmLogicLens.Api.Storage;
using CrmLogicLens.Api.Validation;
using CrmLogicLens.Core;
using Microsoft.Extensions.Options;

namespace CrmLogicLens.Api.Features;

public static class ApiEndpoints
{
    public static IEndpointRouteBuilder MapApiEndpoints(
        this IEndpointRouteBuilder endpoints,
        bool requireAuthorization)
    {
        var group = endpoints.MapGroup("/api/v1")
            .RequireRateLimiting(ApiHostExtensions.ApiRateLimitPolicy);

        if (requireAuthorization)
        {
            group.RequireAuthorization();
        }

        group.MapPost("/snapshots", CreateSnapshotAsync)
            .WithName("CreateSnapshot");
        group.MapGet("/snapshots/{snapshotId:guid}", GetSnapshotAsync)
            .WithName("GetSnapshot");
        group.MapGet("/jobs/{jobId:guid}", GetJobAsync)
            .WithName("GetAnalysisJob");
        group.MapGet("/snapshots/{snapshotId:guid}/evidence", GetEvidenceAsync)
            .WithName("GetEvidence");
        group.MapPost("/chat", ChatAsync)
            .WithName("AskEvidenceQuestion");
        group.MapGet("/capabilities", GetCapabilities)
            .WithName("GetCapabilities");

        return endpoints;
    }

    private static async Task<IResult> CreateSnapshotAsync(
        SnapshotUpload? upload,
        SnapshotIngestionService ingestion,
        CancellationToken cancellationToken)
    {
        var receipt = await ingestion.IngestAsync(upload, cancellationToken);
        return Results.Accepted($"/api/v1/jobs/{receipt.JobId:D}", receipt);
    }

    private static async Task<IResult> GetSnapshotAsync(
        Guid snapshotId,
        IAnalysisStore store,
        CancellationToken cancellationToken)
    {
        var snapshot = await store.GetSnapshotAsync(snapshotId, cancellationToken);
        return snapshot is null
            ? NotFound("Snapshot not found", "No snapshot exists with the supplied identifier.")
            : Results.Ok(snapshot);
    }

    private static async Task<IResult> GetJobAsync(
        Guid jobId,
        IAnalysisStore store,
        CancellationToken cancellationToken)
    {
        var job = await store.GetJobAsync(jobId, cancellationToken);
        return job is null
            ? NotFound("Analysis job not found", "No analysis job exists with the supplied identifier.")
            : Results.Ok(job);
    }

    private static async Task<IResult> GetEvidenceAsync(
        Guid snapshotId,
        IAnalysisStore store,
        CancellationToken cancellationToken)
    {
        var analysis = await store.GetAnalysisAsync(snapshotId, cancellationToken);
        if (analysis is not null)
        {
            return Results.Ok(analysis);
        }

        var snapshot = await store.GetSnapshotAsync(snapshotId, cancellationToken);
        if (snapshot is null)
        {
            return NotFound("Evidence not found", "No snapshot exists with the supplied identifier.");
        }

        var job = await store.GetJobAsync(snapshot.JobId, cancellationToken);
        return Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Analysis is not ready",
            detail: $"Current job status: {job?.Status ?? AnalysisJobStatus.Pending}.",
            extensions: new Dictionary<string, object?>
            {
                ["jobId"] = snapshot.JobId,
                ["status"] = job?.Status ?? AnalysisJobStatus.Pending
            });
    }

    private static async Task<IResult> ChatAsync(
        ChatRequest? request,
        EvidenceQueryService query,
        CancellationToken cancellationToken) =>
        Results.Ok(await query.AnswerAsync(request, cancellationToken));

    private static IResult GetCapabilities(
        IOptions<StorageOptions> storageOptions,
        IOptions<AnalysisQueueOptions> queueOptions,
        SnapshotUploadValidator validator,
        IWebHostEnvironment environment,
        IOptions<SecurityOptions> securityOptions,
        IOptions<DecompilerOptions> decompilerOptions,
        IOptions<AiModelOptions> aiOptions,
        DecompilerProcessService decompiler,
        DiagnosticSkillCatalog diagnosticSkills)
    {
        var storage = storageOptions.Value;
        var security = securityOptions.Value;
        var decompilerLimits = decompilerOptions.Value;
        var ai = aiOptions.Value;

        return Results.Ok(new
        {
            apiVersion = "v1",
            artifactKinds = validator.SupportedMediaTypes.Keys
                .Select(kind => JsonNamingPolicy.CamelCase.ConvertName(kind.ToString()))
                .ToArray(),
            derivedArtifactKinds = new[] { "decompiledCSharp" },
            acceptedMediaTypes = validator.SupportedMediaTypes.ToDictionary(
                item => JsonNamingPolicy.CamelCase.ConvertName(item.Key.ToString()),
                item => item.Value),
            limits = new
            {
                maxArtifactBytes = storage.MaxArtifactBytes,
                maxSnapshotBytes = storage.MaxSnapshotBytes,
                maxRequestBodyBytes = storage.MaxRequestBodyBytes,
                maxArtifactsPerSnapshot = storage.MaxArtifactsPerSnapshot,
                analysisQueueCapacity = queueOptions.Value.Capacity,
                maxQuestionCharacters = 2_000
            },
            safeguards = new
            {
                secureConfigurationExcluded = storage.ExcludeSecureConfiguration,
                pluginAssembliesAreNeverExecuted = true,
                crmDataAccessRequiresExplicitConsent = true,
                crmDataQueriesAreReadOnlyAndBrowserMediated = true,
                customApisAreCollectedOnlyWhenReferencedByCurrentScripts = true,
                runtimeDiagnosticsRequireConsentAndNeverReplayRequests = true,
                runtimeDiagnosticsExcludeRequestBodiesAndCredentials = true,
                crmCredentialsAreNeverAccepted = true,
                contentAddressing = "sha256"
            },
            decompiler = new
            {
                workerConfigured = decompiler.IsConfigured,
                workerAvailable = decompiler.IsAvailable,
                isolation = "external-process-inheriting-service-identity",
                timeoutSeconds = decompilerLimits.TimeoutSeconds,
                maxInputBytes = decompilerLimits.MaxInputBytes,
                maxOutputBytes = decompilerLimits.MaxOutputBytes,
                maxAssembliesPerSnapshot = decompilerLimits.MaxAssembliesPerSnapshot,
                mode = decompilerLimits.EagerAnalysis ? "during-analysis" : "ai-selected-step-on-demand"
            },
            ai = new
            {
                provider = ai.Provider,
                enabled = ai.Enabled,
                configured = ai.Enabled && !string.IsNullOrWhiteSpace(ai.ApiKey),
                model = ai.Model,
                maxContextTokens = ai.MaxContextTokens,
                maxCompletionTokens = ai.MaxCompletionTokens,
                maxInvestigationCompletionTokens = ai.MaxInvestigationCompletionTokens,
                maxEvidenceCharacters = ai.MaxEvidenceCharacters,
                evidenceGrounded = true,
                deterministicFallback = true
            },
            diagnosticSkills = new
            {
                enabled = diagnosticSkills.Skills.Count > 0,
                count = diagnosticSkills.Skills.Count,
                progressiveLoading = true,
                serverEnforcedChecklist = true,
                skills = diagnosticSkills.Skills.Select(skill => new
                {
                    id = skill.Id,
                    skill.Description,
                    skill.Version
                }).ToArray()
            },
            authentication = new
            {
                mode = !environment.IsDevelopment() && security.EnableWindowsAuthentication
                    ? "windows-negotiate"
                    : "anonymous"
            }
        });
    }

    private static IResult NotFound(string title, string detail) =>
        Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: title,
            detail: detail);
}
