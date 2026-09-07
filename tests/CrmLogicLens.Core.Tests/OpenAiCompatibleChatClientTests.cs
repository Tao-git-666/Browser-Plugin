using System.Net;
using System.Text;
using System.Text.Json;
using CrmLogicLens.Api.Configuration;
using CrmLogicLens.Api.Services;
using CrmLogicLens.Api.Storage;
using CrmLogicLens.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CrmLogicLens.Core.Tests;

public sealed class OpenAiCompatibleChatClientTests
{
    [Fact]
    public async Task ExplainAsync_ReusesEquivalentToolArgumentsWithoutRepeatingEvidencePayload()
    {
        var count = 0;
        var handler = new StubHandler(async request =>
        {
            count++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            if (count <= 2)
            {
                var arguments = count == 1 ? "{\"query\":\"审批按钮\",\"limit\":4}" : "{\"limit\":4,\"query\":\"审批按钮\"}";
                return JsonResponse(JsonSerializer.Serialize(new
                {
                    choices = new[] { new { finish_reason = "tool_calls", message = new
                    {
                        tool_calls = new[] { new { id = $"call-{count}", type = "function", function = new { name = "find_business_logic", arguments } } }
                    } } }
                }));
            }
            if (count == 3)
            {
                var messages = body.RootElement.GetProperty("messages");
                using var reused = JsonDocument.Parse(messages[5].GetProperty("content").GetString()!);
                Assert.True(reused.RootElement.GetProperty("reused").GetBoolean());
                Assert.Contains("ribbon-button:approval", messages[3].GetProperty("content").GetString());
                Assert.DoesNotContain("ribbon-button:approval", messages[5].GetProperty("content").GetString());
                return JsonResponse("""{"choices":[{"finish_reason":"stop","message":{"content":"调查已完成"}}]}""");
            }
            return JsonResponse("""{"choices":[{"finish_reason":"stop","message":{"content":"{\"answer\":\"这是提交审批按钮。\",\"evidenceIds\":[\"ribbon-button:approval\"]}"}}]}""");
        });
        var id = Guid.NewGuid();
        var result = await CreateClient(handler).ExplainAsync(new ChatRequest(id, "审批按钮"), CreateSession(id), CancellationToken.None);
        Assert.Equal(4, count);
        Assert.Single(result.Trace!, step => step.ToolName == "find_business_logic");
    }

    [Fact]
    public async Task ExplainAsync_FinalizesCollectedEvidenceOnContextOverflow()
    {
        var count = 0;
        var handler = new StubHandler(async request =>
        {
            count++;
            if (count == 1)
                return JsonResponse("""{"choices":[{"finish_reason":"tool_calls","message":{"tool_calls":[{"id":"find","type":"function","function":{"name":"find_business_logic","arguments":"{\"query\":\"审批按钮\"}"}}]}}]}""");
            if (count == 2)
                return new HttpResponseMessage(HttpStatusCode.RequestEntityTooLarge) { Content = new StringContent("{}") };
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.False(body.RootElement.TryGetProperty("tools", out _));
            return JsonResponse("""{"choices":[{"finish_reason":"stop","message":{"content":"{\"answer\":\"这是提交审批按钮。\",\"evidenceIds\":[\"ribbon-button:approval\"]}"}}]}""");
        });
        var id = Guid.NewGuid();
        var result = await CreateClient(handler).ExplainAsync(new ChatRequest(id, "审批按钮"), CreateSession(id), CancellationToken.None);
        Assert.Equal(3, count);
        Assert.Contains(result.Unknowns, item => item.Contains("上下文"));
    }

    [Fact]
    public async Task ExplainAsync_ExecutesToolAndPreservesReasoningBeforeBusinessAnswer()
    {
        var requestCount = 0;
        var handler = new StubHandler(async request =>
        {
            requestCount++;
            Assert.Equal("https://api.deepseek.test/chat/completions", request.RequestUri?.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            var body = await request.Content!.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            Assert.Equal("deepseek-v4-pro", document.RootElement.GetProperty("model").GetString());
            if (requestCount == 1)
            {
                Assert.Equal(7, document.RootElement.GetProperty("tools").GetArrayLength());
                Assert.Equal(2, document.RootElement.GetProperty("messages").GetArrayLength());
                var systemPrompt = document.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
                Assert.Contains("由问题本身决定回答的结构和长短", systemPrompt, StringComparison.Ordinal);
                return JsonResponse(
                    """
                    {"choices":[{"finish_reason":"tool_calls","message":{"role":"assistant","content":null,"reasoning_content":"preserved-thinking","tool_calls":[{"id":"call-1","type":"function","function":{"name":"find_business_logic","arguments":"{\"query\":\"审批按钮\",\"limit\":4}"}}]}}]}
                    """);
            }

            if (requestCount == 2)
            {
                Assert.Equal(7, document.RootElement.GetProperty("tools").GetArrayLength());
                var messages = document.RootElement.GetProperty("messages");
                Assert.Equal(4, messages.GetArrayLength());
                Assert.Equal("preserved-thinking", messages[2].GetProperty("reasoning_content").GetString());
                Assert.Equal("tool", messages[3].GetProperty("role").GetString());
                Assert.Contains("ribbon-button:approval", messages[3].GetProperty("content").GetString(), StringComparison.Ordinal);
                return JsonResponse(
                    """
                    {"choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"调查已完成"}}]}
                    """);
            }

            Assert.False(document.RootElement.TryGetProperty("tools", out _));
            Assert.Equal(8192, document.RootElement.GetProperty("max_tokens").GetInt32());
            Assert.Equal(2, document.RootElement.GetProperty("messages").GetArrayLength());
            return JsonResponse(
                """
                {"choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"{\"answer\":\"点击后会把当前记录提交审批。\",\"evidenceIds\":[\"ribbon-button:approval\"]}"}}]}
                """);
        });
        var client = CreateClient(handler);
        var snapshotId = Guid.NewGuid();
        var session = CreateSession(snapshotId);

        var result = await client.ExplainAsync(
            new ChatRequest(snapshotId, "审批按钮会做什么？"),
            session,
            CancellationToken.None);

        Assert.Equal(3, requestCount);
        Assert.Contains("点击后会把当前记录提交审批", result.Answer, StringComparison.Ordinal);
        Assert.Equal("点击后会把当前记录提交审批。", result.Answer);
        Assert.DoesNotContain("结论", result.Answer, StringComparison.Ordinal);
        Assert.Single(result.Citations);
        Assert.Equal("ribbon-button:approval", result.Citations[0].NodeId);
        Assert.NotNull(result.Trace);
        Assert.Equal(3, result.Trace.Count);
        Assert.Contains(result.Trace, step => step.ToolName == "find_business_logic");
        Assert.Equal("生成回答", result.Trace[^1].Title);
    }

    [Fact]
    public async Task ExplainAsync_ContinuesWhenProviderReturnsReasoningOnly()
    {
        var requestCount = 0;
        var handler = new StubHandler(async request =>
        {
            requestCount++;
            var body = await request.Content!.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);

            if (requestCount == 1)
            {
                return JsonResponse(
                    """
                    {"choices":[{"finish_reason":"stop","message":{"role":"assistant","content":null,"reasoning_content":"尚未完成的字段调查"}}]}
                    """);
            }

            if (requestCount == 2)
            {
                var messages = document.RootElement.GetProperty("messages");
                Assert.Equal(4, messages.GetArrayLength());
                Assert.Equal("assistant", messages[2].GetProperty("role").GetString());
                Assert.Equal(JsonValueKind.Null, messages[2].GetProperty("content").ValueKind);
                Assert.Equal("尚未完成的字段调查", messages[2].GetProperty("reasoning_content").GetString());
                Assert.Equal("user", messages[3].GetProperty("role").GetString());
                Assert.Contains("继续当前调查", messages[3].GetProperty("content").GetString(), StringComparison.Ordinal);
                return JsonResponse(
                    """
                    {"choices":[{"finish_reason":"tool_calls","message":{"role":"assistant","content":null,"reasoning_content":"继续读取按钮证据","tool_calls":[{"id":"call-1","type":"function","function":{"name":"find_business_logic","arguments":"{\"query\":\"审批按钮\",\"limit\":4}"}}]}}]}
                    """);
            }

            if (requestCount == 3)
            {
                var messages = document.RootElement.GetProperty("messages");
                Assert.Equal("尚未完成的字段调查", messages[2].GetProperty("reasoning_content").GetString());
                Assert.Equal("继续读取按钮证据", messages[4].GetProperty("reasoning_content").GetString());
                Assert.Equal("tool", messages[5].GetProperty("role").GetString());
                return JsonResponse(
                    """
                    {"choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"调查已完成"}}]}
                    """);
            }

            Assert.False(document.RootElement.TryGetProperty("tools", out _));
            return JsonResponse(
                """
                {"choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"{\"answer\":\"点击后会把当前记录提交审批。\",\"evidenceIds\":[\"ribbon-button:approval\"]}"}}]}
                """);
        });
        var client = CreateClient(handler);
        var snapshotId = Guid.NewGuid();

        var result = await client.ExplainAsync(
            new ChatRequest(snapshotId, "审批按钮会做什么？"),
            CreateSession(snapshotId),
            CancellationToken.None);

        Assert.Equal(4, requestCount);
        Assert.Equal("点击后会把当前记录提交审批。", result.Answer);
        Assert.Single(result.Citations);
        Assert.Equal("ribbon-button:approval", result.Citations[0].NodeId);
    }

    [Fact]
    public async Task ExplainAsync_RejectsClaimWithCitationNotExposedByTools()
    {
        var requestCount = 0;
        var handler = new StubHandler(_ =>
        {
            requestCount++;
            return Task.FromResult(requestCount == 1
                ? JsonResponse(
                    """
                    {"choices":[{"finish_reason":"tool_calls","message":{"content":null,"reasoning_content":"thinking","tool_calls":[{"id":"call-1","type":"function","function":{"name":"find_business_logic","arguments":"{\"query\":\"审批按钮\"}"}}]}}]}
                    """)
                : requestCount == 2
                    ? JsonResponse(
                    """
                    {"choices":[{"finish_reason":"stop","message":{"content":"调查已完成"}}]}
                    """)
                    : JsonResponse(
                    """
                    {"choices":[{"finish_reason":"stop","message":{"content":"{\"answer\":\"已完成审批。\",\"evidenceIds\":[\"invented:99\"]}"}}]}
                    """));
        });
        var client = CreateClient(handler);
        var snapshotId = Guid.NewGuid();

        await Assert.ThrowsAsync<AiProviderException>(() => client.ExplainAsync(
            new ChatRequest(snapshotId, "审批按钮会做什么？"),
            CreateSession(snapshotId),
            CancellationToken.None));
    }

    [Fact]
    public async Task ExplainAsync_RepairsOneMalformedFinalStructureWithoutAddingEvidence()
    {
        var requestCount = 0;
        var handler = new StubHandler(async request =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                return JsonResponse(
                    """
                    {"choices":[{"finish_reason":"tool_calls","message":{"content":null,"reasoning_content":"thinking-1","tool_calls":[{"id":"call-1","type":"function","function":{"name":"find_business_logic","arguments":"{\"query\":\"审批按钮\"}"}}]}}]}
                    """);
            }
            if (requestCount == 2)
            {
                return JsonResponse(
                    """
                    {"choices":[{"finish_reason":"stop","message":{"content":"调查已完成","reasoning_content":"thinking-2"}}]}
                    """);
            }

            var body = await request.Content!.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            var messages = document.RootElement.GetProperty("messages");
            Assert.Equal(2, messages.GetArrayLength());
            Assert.False(document.RootElement.TryGetProperty("tools", out _));
            if (requestCount == 3)
            {
                return JsonResponse(
                    """
                    {"choices":[{"finish_reason":"stop","message":{"content":"{\"conclusion\":\"点击后提交审批\"}"}}]}
                    """);
            }
            Assert.Contains("严格只输出", messages[0].GetProperty("content").GetString(), StringComparison.Ordinal);
            return JsonResponse(
                """
                {"choices":[{"finish_reason":"stop","message":{"content":"前缀文字 {\"answer\":\"点击后提交审批。\",\"evidenceIds\":[\"ribbon-button:approval\"]} 后缀文字"}}]}
                """);
        });
        var client = CreateClient(handler);
        var snapshotId = Guid.NewGuid();

        var result = await client.ExplainAsync(
            new ChatRequest(snapshotId, "审批按钮会做什么？"),
            CreateSession(snapshotId),
            CancellationToken.None);

        Assert.Equal(4, requestCount);
        Assert.Contains("点击后提交审批", result.Answer, StringComparison.Ordinal);
        Assert.Single(result.Citations);
    }

    [Fact]
    public async Task ExplainAsync_FinalizesFromCompactEvidenceWhenInvestigationDraftHitsLengthLimit()
    {
        var requestCount = 0;
        var handler = new StubHandler(async request =>
        {
            requestCount++;
            var body = await request.Content!.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            if (requestCount == 1)
            {
                return JsonResponse(
                    """
                    {"choices":[{"finish_reason":"tool_calls","message":{"content":null,"tool_calls":[{"id":"find","type":"function","function":{"name":"find_business_logic","arguments":"{\"query\":\"审批按钮\"}"}}]}}]}
                    """);
            }
            if (requestCount == 2)
            {
                return JsonResponse(
                    """
                    {"choices":[{"finish_reason":"length","message":{"content":"这是一个被截断的调查草稿"}}]}
                    """);
            }

            Assert.False(document.RootElement.TryGetProperty("tools", out _));
            var context = document.RootElement.GetProperty("messages")[1].GetProperty("content").GetString();
            Assert.Contains("ribbon-button:approval", context, StringComparison.Ordinal);
            return JsonResponse(
                """
                {"choices":[{"finish_reason":"stop","message":{"content":"{\"answer\":\"点击后会提交当前记录进入审批。\",\"evidenceIds\":[\"ribbon-button:approval\"]}"}}]}
                """);
        });
        var client = CreateClient(handler);
        var snapshotId = Guid.NewGuid();

        var result = await client.ExplainAsync(
            new ChatRequest(snapshotId, "审批按钮会做什么？"),
            CreateSession(snapshotId),
            CancellationToken.None);

        Assert.Equal(3, requestCount);
        Assert.Contains("提交当前记录进入审批", result.Answer, StringComparison.Ordinal);
        Assert.Single(result.Citations);
    }

    [Fact]
    public async Task ExplainAsync_StopsNewToolsAtInvestigationDeadlineAndPreservesReasoningBetweenRequests()
    {
        var requestCount = 0;
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero));
        var handler = new StubHandler(async request =>
        {
            requestCount++;
            var body = await request.Content!.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            if (requestCount == 1)
            {
                clock.Advance(TimeSpan.FromMinutes(5));
                return JsonResponse(
                    """
                    {"choices":[{"finish_reason":"tool_calls","message":{"content":null,"reasoning_content":"尚未完成的调查状态","tool_calls":[{"id":"find-1","type":"function","function":{"name":"find_business_logic","arguments":"{\"query\":\"审批按钮\"}"}}]}}]}
                    """);
            }
            if (requestCount == 2)
            {
                var messages = document.RootElement.GetProperty("messages");
                Assert.Equal("尚未完成的调查状态", messages[2].GetProperty("reasoning_content").GetString());
                Assert.Equal("tool", messages[3].GetProperty("role").GetString());
                clock.Advance(TimeSpan.FromMinutes(8.5));
                return JsonResponse(
                    """
                    {"choices":[{"finish_reason":"tool_calls","message":{"content":null,"reasoning_content":"继续调查","tool_calls":[{"id":"trace-1","type":"function","function":{"name":"trace_evidence","arguments":"{\"node_id\":\"ribbon-button:approval\"}"}}]}}]}
                    """);
            }
            Assert.False(document.RootElement.TryGetProperty("tools", out _));
            return JsonResponse(
                """
                {"choices":[{"finish_reason":"stop","message":{"content":"{\"answer\":\"当前证据已定位到提交审批按钮。\",\"evidenceIds\":[\"ribbon-button:approval\"]}"}}]}
                """);
        });
        var client = CreateClient(handler, clock);
        var snapshotId = Guid.NewGuid();

        var result = await client.ExplainAsync(
            new ChatRequest(snapshotId, "审批按钮会做什么？"),
            CreateSession(snapshotId),
            CancellationToken.None);

        Assert.Equal(3, requestCount);
        Assert.Contains("提交审批按钮", result.Answer, StringComparison.Ordinal);
        Assert.Single(result.Citations);
        Assert.Contains(result.Unknowns, item => item.Contains("预留 90 秒", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Trace!, step => step.ToolName == "trace_evidence");
        Assert.Equal("completed", result.Trace![^1].Status);
    }

    [Fact]
    public async Task ExplainAsync_RetriesTransientFinalizerFailureWithSmallerEvidencePackage()
    {
        var requestCount = 0;
        var handler = new StubHandler(async request =>
        {
            requestCount++;
            var body = await request.Content!.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            if (requestCount == 1)
            {
                return JsonResponse(
                    """
                    {"choices":[{"finish_reason":"tool_calls","message":{"content":null,"tool_calls":[{"id":"find","type":"function","function":{"name":"find_business_logic","arguments":"{\"query\":\"审批按钮\"}"}}]}}]}
                    """);
            }
            if (requestCount == 2)
            {
                return JsonResponse(
                    """
                    {"choices":[{"finish_reason":"stop","message":{"content":"调查已完成"}}]}
                    """);
            }
            if (requestCount == 3)
            {
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent(
                        "{\"error\":{\"message\":\"短暂限流\"}}",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            Assert.False(document.RootElement.TryGetProperty("tools", out _));
            return JsonResponse(
                """
                {"choices":[{"finish_reason":"stop","message":{"content":"{\"answer\":\"点击后会提交当前记录进入审批。\",\"evidenceIds\":[\"ribbon-button:approval\"]}"}}]}
                """);
        });
        var client = CreateClient(handler);
        var snapshotId = Guid.NewGuid();

        var result = await client.ExplainAsync(
            new ChatRequest(snapshotId, "审批按钮会做什么？"),
            CreateSession(snapshotId),
            CancellationToken.None);

        Assert.Equal(4, requestCount);
        Assert.Contains("提交当前记录", result.Answer, StringComparison.Ordinal);
        Assert.Single(result.Citations);
    }

    [Fact]
    public async Task ExplainAsync_ReturnsBrowserDataRequestWhenConsentedQueryNeedsExecution()
    {
        var handler = new StubHandler(_ => Task.FromResult(JsonResponse(
            """
            {"choices":[{"finish_reason":"tool_calls","message":{"content":null,"tool_calls":[{"id":"data-1","type":"function","function":{"name":"query_crm_data","arguments":"{\"entity\":\"account\",\"select\":[\"name\"],\"top\":1,\"current_record\":true,\"purpose\":\"读取当前客户名称\"}"}}]}}]}
            """)));
        var client = CreateClient(handler);
        var snapshotId = Guid.NewGuid();

        var response = await client.ExplainAsync(
            new ChatRequest(snapshotId, "当前客户叫什么？", DataAccessConsent: true),
            CreateSession(snapshotId, dataAccessConsent: true),
            CancellationToken.None);

        Assert.Equal(string.Empty, response.Answer);
        Assert.Single(response.DataRequests!);
        Assert.Equal("account", response.DataRequests![0].Entity);
        Assert.True(response.DataRequests![0].CurrentRecord);
        Assert.Contains(response.Trace!, step => step.Status == "requested");
    }

    [Fact]
    public async Task ExplainAsync_ResumesServerSideReasoningAfterBrowserDataCallback()
    {
        var requestCount = 0;
        string? citationId = null;
        var handler = new StubHandler(async request =>
        {
            requestCount++;
            var body = await request.Content!.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            if (requestCount == 1)
            {
                return JsonResponse(
                    """
                    {"choices":[{"finish_reason":"tool_calls","message":{"content":null,"reasoning_content":"等待当前客户数据后继续","tool_calls":[{"id":"data-1","type":"function","function":{"name":"query_crm_data","arguments":"{\"entity\":\"account\",\"select\":[\"name\"],\"current_record\":true,\"purpose\":\"读取当前客户名称\"}"}}]}}]}
                    """);
            }
            if (requestCount == 2)
            {
                var messages = document.RootElement.GetProperty("messages");
                Assert.Equal("等待当前客户数据后继续", messages[2].GetProperty("reasoning_content").GetString());
                Assert.Equal("tool", messages[3].GetProperty("role").GetString());
                Assert.Contains("浏览器已完成", messages[4].GetProperty("content").GetString(), StringComparison.Ordinal);
                return JsonResponse(
                    """
                    {"choices":[{"finish_reason":"tool_calls","message":{"content":null,"reasoning_content":"使用返回数据形成结论","tool_calls":[{"id":"data-2","type":"function","function":{"name":"query_crm_data","arguments":"{\"entity\":\"account\",\"select\":[\"name\"],\"current_record\":true,\"purpose\":\"读取当前客户名称\"}"}}]}}]}
                    """);
            }
            if (requestCount == 3)
            {
                var messages = document.RootElement.GetProperty("messages");
                Assert.Equal("使用返回数据形成结论", messages[5].GetProperty("reasoning_content").GetString());
                Assert.Contains("Contoso", messages[6].GetProperty("content").GetString(), StringComparison.Ordinal);
                return JsonResponse(
                    """
                    {"choices":[{"finish_reason":"stop","message":{"content":"调查完成"}}]}
                    """);
            }
            Assert.False(document.RootElement.TryGetProperty("tools", out _));
            return JsonResponse(JsonSerializer.Serialize(new
            {
                choices = new[]
                {
                    new
                    {
                        finish_reason = "stop",
                        message = new
                        {
                            content = JsonSerializer.Serialize(new
                            {
                                answer = "当前客户名称是 Contoso。",
                                evidenceIds = new[] { citationId }
                            })
                        }
                    }
                }
            }));
        });
        var client = CreateClient(handler);
        var snapshotId = Guid.NewGuid();
        var first = await client.ExplainAsync(
            new ChatRequest(snapshotId, "当前客户叫什么？", DataAccessConsent: true),
            CreateSession(snapshotId, dataAccessConsent: true),
            CancellationToken.None);
        var dataRequest = Assert.Single(first.DataRequests!);
        Assert.NotNull(first.ContinuationId);
        citationId = $"crm-data:{dataRequest.RequestId}";

        var resumed = await client.ExplainAsync(
            new ChatRequest(
                snapshotId,
                "当前客户叫什么？",
                DataAccessConsent: true,
                DataResults:
                [
                    new CrmDataQueryResult(
                        dataRequest.RequestId,
                        true,
                        "{\"value\":[{\"name\":\"Contoso\"}]}" )
                ],
                ContinuationId: first.ContinuationId),
            CreateSession(snapshotId, dataAccessConsent: true),
            CancellationToken.None);

        Assert.Equal(4, requestCount);
        Assert.Equal("当前客户名称是 Contoso。", resumed.Answer);
        Assert.Contains(resumed.Citations, citation => citation.NodeId == citationId);
    }

    [Fact]
    public async Task ExplainAsync_ReturnsCurrentFormValueRequestForUnsavedFields()
    {
        var handler = new StubHandler(_ => Task.FromResult(JsonResponse(
            """
            {"choices":[{"finish_reason":"tool_calls","message":{"content":null,"tool_calls":[{"id":"form-1","type":"function","function":{"name":"read_current_form_values","arguments":"{\"fields\":[\"new_servicetype\"],\"purpose\":\"确认用户刚选择但尚未保存的服务类型\"}"}}]}}]}
            """)));
        var client = CreateClient(handler);
        var snapshotId = Guid.NewGuid();

        var response = await client.ExplainAsync(
            new ChatRequest(snapshotId, "我刚把服务类型改成维修，为什么字段还没有显示？", DataAccessConsent: true),
            CreateSession(snapshotId, dataAccessConsent: true),
            CancellationToken.None);

        Assert.Equal(string.Empty, response.Answer);
        var request = Assert.Single(response.FormValueRequests!);
        Assert.Equal("new_servicetype", Assert.Single(request.Fields));
        Assert.Contains(response.Trace!, step =>
            step.ToolName == "read_current_form_values" && step.Status == "requested");
    }

    [Fact]
    public async Task ExplainAsync_BlocksPrematureAnswerUntilSkillChecklistIsComplete()
    {
        var requestCount = 0;
        var handler = new StubHandler(async request =>
        {
            requestCount++;
            var body = await request.Content!.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            if (requestCount <= 5)
            {
                Assert.Equal(10, document.RootElement.GetProperty("tools").GetArrayLength());
            }
            if (requestCount == 1)
            {
                return JsonResponse(
                    """
                    {"choices":[{"finish_reason":"stop","message":{"content":"{\"answer\":\"猜测是权限问题。\",\"evidenceIds\":[\"ribbon-button:approval\"]}"}}]}
                    """);
            }
            if (requestCount == 2)
            {
                var messages = document.RootElement.GetProperty("messages");
                Assert.Contains(
                    "诊断流程尚未完成",
                    messages[messages.GetArrayLength() - 1].GetProperty("content").GetString(),
                    StringComparison.Ordinal);
                return JsonResponse(
                    """
                    {"choices":[{"finish_reason":"tool_calls","message":{"content":null,"tool_calls":[
                      {"id":"skill-search","type":"function","function":{"name":"search_diagnostic_skills","arguments":"{\"query\":\"审批按钮会做什么\"}"}},
                      {"id":"skill-read","type":"function","function":{"name":"read_diagnostic_skill","arguments":"{\"skill_id\":\"explain-current-logic\"}"}}
                    ]}}]}
                    """);
            }
            if (requestCount == 3)
            {
                return JsonResponse(
                    """
                    {"choices":[{"finish_reason":"tool_calls","message":{"content":null,"tool_calls":[{"id":"find","type":"function","function":{"name":"find_business_logic","arguments":"{\"query\":\"审批按钮\"}"}}]}}]}
                    """);
            }
            if (requestCount == 4)
            {
                return JsonResponse(
                    """
                    {"choices":[{"finish_reason":"tool_calls","message":{"content":null,"tool_calls":[{"id":"check","type":"function","function":{"name":"check_diagnostic_progress","arguments":"{}"}}]}}]}
                    """);
            }
            if (requestCount == 5)
            {
                return JsonResponse(
                    """
                    {"choices":[{"finish_reason":"stop","message":{"content":"调查已完成"}}]}
                    """);
            }
            Assert.False(document.RootElement.TryGetProperty("tools", out _));
            return JsonResponse(
                """
                {"choices":[{"finish_reason":"stop","message":{"content":"{\"answer\":\"这是当前窗体上的提交审批按钮。\",\"evidenceIds\":[\"ribbon-button:approval\"]}"}}]}
                """);
        });
        var client = CreateClient(handler);
        var snapshotId = Guid.NewGuid();
        var catalog = new DiagnosticSkillCatalog(
        [
            new DiagnosticSkill(
                "explain-current-logic",
                "解释当前业务逻辑。",
                "1.0",
                ["有什么用"],
                ["find_business_logic"],
                [],
                [],
                "先找到相关入口。",
                true,
                "test")
        ]);

        var result = await client.ExplainAsync(
            new ChatRequest(snapshotId, "审批按钮会做什么？"),
            CreateSession(snapshotId, diagnosticSkills: catalog),
            CancellationToken.None);

        Assert.Equal(6, requestCount);
        Assert.Contains("提交审批按钮", result.Answer, StringComparison.Ordinal);
        Assert.Contains(result.Trace!, step => step.Title == "采用诊断 Skill");
        Assert.Contains(result.Trace!, step => step.Title == "检查 Skill 必查项" && step.Status == "completed");
    }

    private static EvidenceToolSession CreateSession(
        Guid snapshotId,
        bool dataAccessConsent = false,
        DiagnosticSkillCatalog? diagnosticSkills = null)
    {
        var context = new CrmPageContext(
            "https://crm.example.test/org",
            "org",
            "9.1",
            "v9.1",
            "entityrecord",
            "account",
            "11111111-2222-3333-4444-555555555555",
            "form-1",
            null,
            "客户");
        var snapshot = new StoredSnapshot(
            snapshotId,
            Guid.NewGuid(),
            context,
            [],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        var graph = new EvidenceGraph(
        [
            new EvidenceNode(
                "ribbon-button:approval",
                "RibbonButton",
                "提交审批",
                "当前窗体上的提交审批按钮。",
                "account.ribbon.xml",
                "button SubmitApproval",
                EvidenceConfidence.Confirmed,
                new Dictionary<string, string> { ["Command"] = "SubmitApproval.Command" })
        ],
        [],
        []);
        return new EvidenceToolSession(
            new StubStore(snapshot),
            snapshot,
            graph,
            dataAccessConsent: dataAccessConsent,
            diagnosticSkills: diagnosticSkills);
    }

    private static OpenAiCompatibleChatClient CreateClient(
        HttpMessageHandler handler,
        TimeProvider? timeProvider = null)
    {
        var effectiveTimeProvider = timeProvider ?? TimeProvider.System;
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.deepseek.test/")
        };
        return new OpenAiCompatibleChatClient(
            httpClient,
            Options.Create(new AiModelOptions
            {
                Enabled = true,
                Provider = "DeepSeek",
                BaseUrl = "https://api.deepseek.test",
                Model = "deepseek-v4-pro",
                ApiKey = "test-key",
                MaxInvestigationSeconds = 900,
                MaxToolResultCharacters = 48_000
            }),
            effectiveTimeProvider,
            new AiInvestigationContinuationStore(effectiveTimeProvider),
            NullLogger<OpenAiCompatibleChatClient>.Instance);
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }

    private static HttpResponseMessage JsonResponse(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request);
    }

    private sealed class StubStore(StoredSnapshot snapshot) : IAnalysisStore
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<StoredArtifact> StoreArtifactAsync(PreparedArtifact artifact, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveSnapshotAsync(StoredSnapshot value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<StoredSnapshot?> GetSnapshotAsync(Guid snapshotId, CancellationToken cancellationToken = default) => Task.FromResult<StoredSnapshot?>(snapshot);
        public Task<SnapshotUpload?> LoadSnapshotUploadAsync(Guid snapshotId, CancellationToken cancellationToken = default) => Task.FromResult<SnapshotUpload?>(null);
        public Task<ArtifactUpload?> LoadArtifactAsync(Guid snapshotId, ArtifactKind kind, string name, CancellationToken cancellationToken = default) => Task.FromResult<ArtifactUpload?>(null);
        public Task SaveJobAsync(AnalysisJob job, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AnalysisJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default) => Task.FromResult<AnalysisJob?>(null);
        public Task<IReadOnlyList<AnalysisJob>> GetRecoverableJobsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AnalysisJob>>([]);
        public Task SaveAnalysisAsync(AnalysisResult result, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AnalysisResult?> GetAnalysisAsync(Guid snapshotId, CancellationToken cancellationToken = default) => Task.FromResult<AnalysisResult?>(null);
        public Task<bool> CheckWritableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
