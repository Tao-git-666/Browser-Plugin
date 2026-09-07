using System.Text.Json;
using System.Threading.Channels;
using CrmLogicLens.Api.Configuration;
using CrmLogicLens.Api.Errors;
using CrmLogicLens.Api.Services;
using CrmLogicLens.Api.Storage;
using CrmLogicLens.Api.Validation;
using CrmLogicLens.Core;
using Microsoft.AspNetCore.Http.Features;
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
        group.MapPost("/chat/stream", ChatStreamAsync)
            .WithName("StreamEvidenceQuestion");
        group.MapGet("/capabilities", GetCapabilities)
            .WithName("GetCapabilities");
        group.MapPost("/code-library/batches/stream", ImportCodeLibraryAsync);

        return endpoints;
    }

    private static async Task ImportCodeLibraryAsync(CodeLibraryBatch batch, EnvironmentCodeLibrary library,
        HttpContext context, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        context.Response.ContentType = "application/x-ndjson; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache, no-transform";
        context.Response.Headers.Append("X-Accel-Buffering", "no");
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var work = library.ImportAsync(batch, linked.Token);
        async Task Emit(object value)
        {
            await context.Response.WriteAsync(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)) + "\n", cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);
        }
        try
        {
            while (!work.IsCompleted)
            {
                await Emit(new { type = "progress", step = new { title = "建立环境代码索引", summary = "正在反编译或复用已缓存的 DLL，并关联注册步骤。" } });
                await Task.WhenAny(work, Task.Delay(5000, cancellationToken));
                cancellationToken.ThrowIfCancellationRequested();
            }
            await Emit(new { type = "result", response = await work });
        }
        catch (ApiInputException exception) { await Emit(new { type = "error", error = exception.Message }); }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            loggerFactory.CreateLogger("CrmLogicLens.CodeLibrary").LogWarning(exception, "Code library batch import failed.");
            await Emit(new { type = "error", error = "该程序集同步失败，已同步部分仍可使用，请重试。" });
        }
        finally
        {
            linked.Cancel();
            try { await work; } catch { /* already reported or client disconnected */ }
        }
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

    private static async Task ChatStreamAsync(
        ChatRequest? request,
        EvidenceQueryService query,
        HttpContext context,
        IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> jsonOptions,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/x-ndjson; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache, no-transform";
        context.Response.Headers.Append("X-Accel-Buffering", "no");
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var events = Channel.CreateUnbounded<ChatStreamEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });
        var logger = loggerFactory.CreateLogger("CrmLogicLens.ChatStream");

        async Task ProduceAsync()
        {
            try
            {
                events.Writer.TryWrite(new ChatStreamEvent(
                    "progress",
                    new AnalysisTraceStep(0, "开始分析", "正在理解问题并准备当前窗体证据。", null, "active")));
                var response = await query.AnswerAsync(
                    request,
                    cancellationToken,
                    step => events.Writer.TryWrite(new ChatStreamEvent("progress", step)));
                events.Writer.TryWrite(new ChatStreamEvent("result", Response: response));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The browser closed the request; no terminal event can be delivered.
            }
            catch (ApiInputException exception)
            {
                events.Writer.TryWrite(new ChatStreamEvent(
                    "error",
                    Error: exception.Message,
                    StatusCode: exception.StatusCode));
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Streaming chat failed with trace identifier {TraceIdentifier}.",
                    context.TraceIdentifier);
                events.Writer.TryWrite(new ChatStreamEvent(
                    "error",
                    Error: $"分析请求未完成，请查看服务器日志。跟踪编号：{context.TraceIdentifier}",
                    StatusCode: StatusCodes.Status500InternalServerError));
            }
            finally
            {
                events.Writer.TryComplete();
            }
        }

        var producer = ProduceAsync();
        await foreach (var item in events.Reader.ReadAllAsync(cancellationToken))
        {
            var json = JsonSerializer.Serialize(item, jsonOptions.Value.SerializerOptions);
            await context.Response.WriteAsync(json + "\n", cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);
        }
        await producer;
    }

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
            streamingChat = new
            {
                enabled = true,
                protocol = "ndjson",
                path = "/api/v1/chat/stream"
            },
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
                maxInvestigationSeconds = ai.MaxInvestigationSeconds,
                finalAnswerReserveSeconds = Math.Min(ai.FinalAnswerReserveSeconds, ai.MaxInvestigationSeconds / 2),
                evidenceGrounded = true,
                deterministicFallback = false
            },
            environmentCodeLibrary = new
            {
                enabled = true,
                requiresExplicitSync = true,
                maxAssemblies = 300,
                assembliesPerBatch = 1,
                cacheKey = "organization-and-dll-sha256",
                registrationData = "sync-time-snapshot"
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

    private sealed record ChatStreamEvent(
        string Type,
        AnalysisTraceStep? Step = null,
        ChatResponse? Response = null,
        string? Error = null,
        int? StatusCode = null);
}
