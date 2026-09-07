using CrmLogicLens.Api.Errors;
using CrmLogicLens.Api.Storage;
using CrmLogicLens.Core;

namespace CrmLogicLens.Api.Services;

public sealed class EvidenceQueryService(
    IAnalysisStore store,
    EvidenceAnswerService answerService,
    OpenAiCompatibleChatClient aiClient,
    DecompiledArtifactService decompiledArtifacts,
    DiagnosticSkillCatalog diagnosticSkills,
    ILogger<EvidenceQueryService> logger,
    EnvironmentCodeLibrary? codeLibrary = null)
{
    public async Task<ChatResponse> AnswerAsync(
        ChatRequest? request,
        CancellationToken cancellationToken,
        Action<AnalysisTraceStep>? progressObserver = null)
    {
        if (request is null)
        {
            throw new ApiInputException("A chat request is required.", "body");
        }

        if (request.SnapshotId == Guid.Empty)
        {
            throw new ApiInputException("snapshotId must be a non-empty GUID.", "snapshotId");
        }

        var question = request.Question?.Trim();
        if (string.IsNullOrWhiteSpace(question) || question.Length > 2_000 || question.IndexOf('\0') >= 0)
        {
            throw new ApiInputException("question must contain between 1 and 2000 characters.", "question");
        }

        var focusComponentId = request.FocusComponentId?.Trim();
        if (focusComponentId is { Length: > 256 } || focusComponentId?.IndexOf('\0') >= 0)
        {
            throw new ApiInputException("focusComponentId cannot exceed 256 characters.", "focusComponentId");
        }
        var continuationId = request.ContinuationId?.Trim().ToLowerInvariant();
        if (continuationId is not null &&
            (continuationId.Length != 32 || continuationId.Any(character => !char.IsAsciiHexDigit(character))))
        {
            throw new ApiInputException("continuationId must be a 32-character hexadecimal identifier.", "continuationId");
        }
        ValidateDataResults(request.DataAccessConsent, request.DataResults);
        ValidateFormValueResults(request.DataAccessConsent, request.FormValueResults);
        ValidateRuntimeDiagnostics(request.DataAccessConsent, request.RuntimeDiagnostics);
        ValidateRuntimeRecording(request.RuntimeRecordingConsent, request.RuntimeRecording);

        var analysis = await store.GetAnalysisAsync(request.SnapshotId, cancellationToken);
        if (analysis is null)
        {
            var snapshot = await store.GetSnapshotAsync(request.SnapshotId, cancellationToken);
            if (snapshot is null)
            {
                throw new ApiInputException(
                    "The requested snapshot does not exist.",
                    "snapshotId",
                    StatusCodes.Status404NotFound);
            }

            var job = await store.GetJobAsync(snapshot.JobId, cancellationToken);
            throw new ApiInputException(
                $"Analysis is not ready. Current job status: {job?.Status ?? AnalysisJobStatus.Pending}.",
                "snapshotId",
                StatusCodes.Status409Conflict);
        }

        var normalizedRequest = request with
        {
            Question = question,
            FocusComponentId = focusComponentId,
            ContinuationId = continuationId
        };
        var evidenceAnswer = answerService.Answer(normalizedRequest, analysis.Graph);
        if (!aiClient.IsConfigured)
        {
            return evidenceAnswer with
            {
                Unknowns = evidenceAnswer.Unknowns
                    .Append("AI 模型服务未配置 API Key，当前回答由本地证据规则生成")
                    .ToArray()
            };
        }

        var storedSnapshot = await store.GetSnapshotAsync(request.SnapshotId, cancellationToken)
            ?? throw new ApiInputException(
                "The requested snapshot does not exist.",
                "snapshotId",
                StatusCodes.Status404NotFound);
        var toolSession = new EvidenceToolSession(
            store,
            storedSnapshot,
            analysis.Graph,
            decompiledArtifacts,
            dataAccessConsent: request.DataAccessConsent,
            dataResults: request.DataResults,
            runtimeDiagnostics: request.RuntimeDiagnostics,
            diagnosticSkills: diagnosticSkills,
            formValueResults: request.FormValueResults,
            runtimeRecording: request.RuntimeRecording,
            progressObserver: progressObserver,
            codeLibrary: codeLibrary,
            environmentLibraryId: request.EnvironmentLibraryId);
        try
        {
            return await aiClient.ExplainAsync(
                normalizedRequest,
                toolSession,
                cancellationToken,
                progressObserver);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is AiProviderException or HttpRequestException or OperationCanceledException)
        {
            logger.LogWarning(exception, "AI answer generation failed for snapshot {SnapshotId}.", request.SnapshotId);
            var reason = exception switch
            {
                OperationCanceledException => "模型或取证阶段超过了本次等待时间。",
                HttpRequestException => "分析服务器与模型服务的连接失败。",
                AiProviderException { StatusCode: 429 } => "模型服务暂时限流。",
                AiProviderException { StatusCode: 401 or 403 } => "模型服务拒绝了身份验证或访问权限。",
                AiProviderException { StatusCode: 413 } => "本次证据超过了模型上下文预算。",
                AiProviderException { StatusCode: 409 } => "此前调查状态已失效，需要重新提问。",
                _ => "模型没有返回可验证的完整业务答案。"
            };
            var fallback = new ChatResponse(
                $"这次分析未完成：{reason}暂时不能可靠回答你的问题。可展开分析过程查看已执行的检查，然后重试。",
                EvidenceConfidence.Unknown,
                toolSession.ExposedCitations,
                [reason]);
            return fallback with
            {
                Trace = toolSession.BuildAnalysisTrace(
                    fallback.Citations.Count,
                    totalDurationMs: 0,
                    aiCompleted: false)
            };
        }
    }

    private static void ValidateDataResults(
        bool consent,
        IReadOnlyList<CrmDataQueryResult>? results)
    {
        if (results is null or { Count: 0 })
        {
            return;
        }
        if (!consent)
        {
            throw new ApiInputException("dataResults require explicit dataAccessConsent.", "dataAccessConsent");
        }
        if (results.Count > 6)
        {
            throw new ApiInputException("dataResults cannot contain more than 6 items.", "dataResults");
        }
        foreach (var result in results)
        {
            if (string.IsNullOrWhiteSpace(result.RequestId) || result.RequestId.Length > 64 ||
                result.RequestId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
            {
                throw new ApiInputException("dataResults contains an invalid requestId.", "dataResults");
            }
            if (result.Json?.Length > 150_000 || result.Error?.Length > 1_000)
            {
                throw new ApiInputException("dataResults contains an oversized value.", "dataResults");
            }
        }
    }

    private static void ValidateRuntimeDiagnostics(
        bool consent,
        IReadOnlyList<RuntimeDiagnosticEvidence>? diagnostics)
    {
        if (diagnostics is null or { Count: 0 }) return;
        if (!consent)
        {
            throw new ApiInputException("runtimeDiagnostics require explicit dataAccessConsent.", "dataAccessConsent");
        }
        if (diagnostics.Count > 10)
        {
            throw new ApiInputException("runtimeDiagnostics cannot contain more than 10 items.", "runtimeDiagnostics");
        }
        foreach (var item in diagnostics)
        {
            if (item.Method.Length is < 1 or > 12 ||
                item.Path.Length is < 1 or > 1_000 ||
                item.Path.IndexOf('\0') >= 0 ||
                item.Status is < 400 or > 599 ||
                item.ResponseBody?.Length > 8_000)
            {
                throw new ApiInputException("runtimeDiagnostics contains an invalid or oversized item.", "runtimeDiagnostics");
            }
        }
    }

    private static void ValidateFormValueResults(
        bool consent,
        IReadOnlyList<FormValueQueryResult>? results)
    {
        if (results is null or { Count: 0 }) return;
        if (!consent)
        {
            throw new ApiInputException("formValueResults require explicit dataAccessConsent.", "dataAccessConsent");
        }
        if (results.Count > 6)
        {
            throw new ApiInputException("formValueResults cannot contain more than 6 items.", "formValueResults");
        }
        foreach (var result in results)
        {
            if (string.IsNullOrWhiteSpace(result.RequestId) || result.RequestId.Length > 64 ||
                result.RequestId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
            {
                throw new ApiInputException("formValueResults contains an invalid requestId.", "formValueResults");
            }
            if (result.Json?.Length > 100_000 || result.Error?.Length > 1_000)
            {
                throw new ApiInputException("formValueResults contains an oversized value.", "formValueResults");
            }
        }
    }

    private static void ValidateRuntimeRecording(
        bool consent,
        IReadOnlyList<RuntimeRecordingEvent>? events)
    {
        if (events is null or { Count: 0 }) return;
        if (!consent)
        {
            throw new ApiInputException(
                "runtimeRecording requires explicit runtimeRecordingConsent.",
                "runtimeRecordingConsent");
        }
        if (events.Count > 100)
        {
            throw new ApiInputException("runtimeRecording cannot contain more than 100 items.", "runtimeRecording");
        }

        var allowedKinds = new HashSet<string>(StringComparer.Ordinal)
        {
            "recording", "user-action", "javascript-error", "promise-rejection",
            "console-error", "ui-error", "http-error", "dataverse-query"
        };
        var totalCharacters = 0;
        foreach (var item in events)
        {
            totalCharacters += item.Summary?.Length ?? 0;
            totalCharacters += item.Details?.Length ?? 0;
            totalCharacters += item.Path?.Length ?? 0;
            if (item.Sequence is < 1 or > 100 ||
                !allowedKinds.Contains(item.Kind) ||
                string.IsNullOrWhiteSpace(item.Summary) ||
                item.Summary.Length > 500 ||
                item.Summary.IndexOf('\0') >= 0 ||
                item.Details?.Length > 8_000 ||
                item.Details?.IndexOf('\0') >= 0 ||
                item.Method?.Length > 12 ||
                item.Path?.Length > 1_000 ||
                item.Path?.IndexOf('\0') >= 0 ||
                item.Status is < 0 or > 599)
            {
                throw new ApiInputException("runtimeRecording contains an invalid or oversized item.", "runtimeRecording");
            }
        }
        if (totalCharacters > 200_000)
        {
            throw new ApiInputException("runtimeRecording exceeds the total character limit.", "runtimeRecording");
        }
    }
}
