using System.Text;
using System.Text.Json;
using CrmLogicLens.Api.Services;
using CrmLogicLens.Api.Storage;
using CrmLogicLens.Core;

namespace CrmLogicLens.Core.Tests;

public sealed class EvidenceToolSessionTests
{
    [Fact]
    public async Task Search_UsesFieldDisplayNameToFindLogicAndKeepsItAheadOfUnrelatedPlugins()
    {
        var fixture = CreateFixture();
        var nodes = fixture.Graph.Nodes.Concat(new[]
        {
            new EvidenceNode("field:idcard", "FieldMetadata", "身份证号", "证件字段", "metadata.json", null,
                EvidenceConfidence.Confirmed, new Dictionary<string, string> { ["Field"] = "new_idcardnumber" }),
            new EvidenceNode("js:idcard", "JavaScriptFunction", "new_idcardnumber_onChange", "校验输入", "form.js", null,
                EvidenceConfidence.Confirmed),
            new EvidenceNode("button:export", "RibbonButton", "导出", "导出按钮", "ribbon.xml", null,
                EvidenceConfidence.Confirmed)
        }).ToArray();
        var session = new EvidenceToolSession(fixture.Store, fixture.Snapshot,
            new EvidenceGraph(nodes, fixture.Graph.Edges, []));
        await session.ExecuteAsync("list_current_entity_plugin_steps", "{}", CancellationToken.None);
        var json = await session.ExecuteAsync("find_business_logic", """{"query":"身份证号的填写逻辑是什么"}""", CancellationToken.None);
        using var found = JsonDocument.Parse(json);
        var ids = found.RootElement.GetProperty("matches").EnumerateArray()
            .Select(item => item.GetProperty("nodeId").GetString()).ToArray();
        Assert.Contains("field:idcard", ids);
        Assert.Contains("js:idcard", ids);
        Assert.DoesNotContain("button:export", ids);

        using var final = JsonDocument.Parse(JsonSerializer.Serialize(session.BuildFinalAnswerContext("身份证号的填写逻辑是什么", 4096)));
        Assert.Equal("find_business_logic", final.RootElement.GetProperty("toolEvidence")[0].GetProperty("tool").GetString());
        Assert.Equal("field:idcard", final.RootElement.GetProperty("allowedEvidence")[0].GetProperty("nodeId").GetString());
    }

    [Fact]
    public async Task Search_ButtonCategoryDoesNotMatchUnrelatedNodes()
    {
        var fixture = CreateFixture();
        var json = await fixture.Session.ExecuteAsync("find_business_logic",
            """{"query":"不存在的派工按钮"}""", CancellationToken.None);
        using var found = JsonDocument.Parse(json);
        Assert.Empty(found.RootElement.GetProperty("matches").EnumerateArray());
    }

    [Fact]
    public async Task ExecuteAsync_ReportsLiveAndCompletedToolStatus()
    {
        var fixture = CreateFixture();
        var progress = new List<AnalysisTraceStep>();
        fixture.Session.SetProgressObserver(progress.Add);

        await fixture.Session.ExecuteAsync(
            "read_javascript_function",
            "{\"function_name\":\"form_onLoad\"}",
            CancellationToken.None);

        Assert.True(progress.Count >= 2);
        Assert.Equal("active", progress[0].Status);
        Assert.Equal("read_javascript_function", progress[0].ToolName);
        Assert.Equal("读取相关窗体脚本", progress[0].Title);
        Assert.Equal(progress[0].Sequence, progress[^1].Sequence);
        Assert.NotEqual("active", progress[^1].Status);
    }

    [Fact]
    public async Task PluginTools_AreStrictlyLimitedToCurrentEntity()
    {
        var fixture = CreateFixture();
        var listed = await fixture.Session.ExecuteAsync(
            "list_current_entity_plugin_steps",
            "{}",
            CancellationToken.None);
        using var listDocument = JsonDocument.Parse(listed);
        var steps = listDocument.RootElement.GetProperty("steps");

        Assert.Single(steps.EnumerateArray());
        Assert.Equal("step:current", steps[0].GetProperty("nodeId").GetString());

        var rejected = await fixture.Session.ExecuteAsync(
            "read_decompiled_plugin",
            "{\"plugin_step_id\":\"step:other\"}",
            CancellationToken.None);
        using var rejectedDocument = JsonDocument.Parse(rejected);
        Assert.Contains(
            "不会读取其他实体",
            rejectedDocument.RootElement.GetProperty("error").GetString(),
            StringComparison.Ordinal);
        Assert.Empty(fixture.Store.ArtifactReads);
    }

    [Fact]
    public async Task ReadDecompiledPlugin_UsesStepRelationshipAndReturnsBoundedRedactedExcerpt()
    {
        var fixture = CreateFixture();
        var result = await fixture.Session.ExecuteAsync(
            "read_decompiled_plugin",
            "{\"plugin_step_id\":\"step:current\",\"search\":\"Update\"}",
            CancellationToken.None);

        using var resultDocument = JsonDocument.Parse(result);
        var excerpt = resultDocument.RootElement.GetProperty("excerpt").GetString();
        Assert.Contains("service.Update", excerpt, StringComparison.Ordinal);
        Assert.Contains("[已隐藏疑似密钥的代码行]", excerpt, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret-value", excerpt, StringComparison.Ordinal);
        Assert.Single(fixture.Store.ArtifactReads);
        Assert.Contains(fixture.Session.ExposedCitations, citation => citation.NodeId == "step:current");
        Assert.Contains(fixture.Session.ExposedCitations, citation => citation.NodeId == "behavior:update");
        var trace = fixture.Session.BuildAnalysisTrace(
            fixture.Session.ExposedCitations.Count,
            totalDurationMs: 1500);
        Assert.Contains(trace, step => step.ToolName == "read_decompiled_plugin" && step.Status == "completed");
        Assert.Contains(trace, step => step.Summary.Contains("插件片段", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiscoveryTools_DoNotExposeLegacyManagedInventory()
    {
        var fixture = CreateFixture();
        var found = await fixture.Session.ExecuteAsync(
            "find_business_logic",
            "{\"query\":\"Newtonsoft plugin\",\"kinds\":[\"ManagedMethod\"]}",
            CancellationToken.None);
        using var foundDocument = JsonDocument.Parse(found);
        Assert.Empty(foundDocument.RootElement.GetProperty("matches").EnumerateArray());

        var traced = await fixture.Session.ExecuteAsync(
            "trace_evidence",
            "{\"node_id\":\"method:legacy\"}",
            CancellationToken.None);
        using var tracedDocument = JsonDocument.Parse(traced);
        Assert.False(tracedDocument.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("技术目录", tracedDocument.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CrmDataTool_RequiresConsentAndUsesBrowserSuppliedResult()
    {
        var fixture = CreateFixture();
        Assert.Equal(7, fixture.Session.GetToolDefinitions().Length);

        var consented = new EvidenceToolSession(
            fixture.Store,
            fixture.Snapshot,
            fixture.Graph,
            dataAccessConsent: true);
        Assert.Equal(9, consented.GetToolDefinitions().Length);
        const string arguments = """
            {"entity":"account","select":["name","accountid"],"top":5,"purpose":"确认当前客户名称"}
            """;
        var pendingJson = await consented.ExecuteAsync(
            "query_crm_data",
            arguments,
            CancellationToken.None);
        using var pendingDocument = JsonDocument.Parse(pendingJson);
        Assert.True(pendingDocument.RootElement.GetProperty("requiresClientData").GetBoolean());
        var pending = Assert.Single(consented.PendingDataRequests);
        Assert.Contains(consented.BuildAnalysisTrace(0, 10), step =>
            step.ToolName == "query_crm_data" && step.Status == "requested");

        var supplied = new EvidenceToolSession(
            fixture.Store,
            fixture.Snapshot,
            fixture.Graph,
            dataAccessConsent: true,
            dataResults:
            [
                new CrmDataQueryResult(
                    pending.RequestId,
                    true,
                    "{\"value\":[{\"accountid\":\"1\",\"name\":\"Contoso\"}]}")
            ]);
        var suppliedJson = await supplied.ExecuteAsync(
            "query_crm_data",
            arguments,
            CancellationToken.None);
        using var suppliedDocument = JsonDocument.Parse(suppliedJson);
        Assert.True(suppliedDocument.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            "Contoso",
            suppliedDocument.RootElement.GetProperty("data").GetProperty("value")[0].GetProperty("name").GetString());
        Assert.Contains(supplied.ExposedCitations, citation => citation.NodeId == $"crm-data:{pending.RequestId}");
    }

    [Fact]
    public async Task CurrentFormValuesTool_ReturnsUnsavedBrowserValuesWithDirtyState()
    {
        var fixture = CreateFixture();
        var consented = new EvidenceToolSession(
            fixture.Store,
            fixture.Snapshot,
            fixture.Graph,
            dataAccessConsent: true);
        const string arguments = """
            {"fields":["new_servicetype","new_description"],"purpose":"确认用户尚未保存的服务类型和说明"}
            """;

        var pendingJson = await consented.ExecuteAsync(
            "read_current_form_values",
            arguments,
            CancellationToken.None);
        using var pendingDocument = JsonDocument.Parse(pendingJson);
        Assert.True(pendingDocument.RootElement.GetProperty("requiresClientData").GetBoolean());
        var pending = Assert.Single(consented.PendingFormValueRequests);
        Assert.Equal(["new_description", "new_servicetype"], pending.Fields);

        var supplied = new EvidenceToolSession(
            fixture.Store,
            fixture.Snapshot,
            fixture.Graph,
            dataAccessConsent: true,
            formValueResults:
            [
                new FormValueQueryResult(
                    pending.RequestId,
                    true,
                    "{\"ok\":true,\"formDirty\":true,\"values\":[{\"field\":\"new_servicetype\",\"value\":2,\"text\":\"维修\",\"isDirty\":true}]}" )
            ]);
        var suppliedJson = await supplied.ExecuteAsync(
            "read_current_form_values",
            arguments,
            CancellationToken.None);
        using var suppliedDocument = JsonDocument.Parse(suppliedJson);
        Assert.True(suppliedDocument.RootElement.GetProperty("ok").GetBoolean());
        var data = suppliedDocument.RootElement.GetProperty("data");
        Assert.True(data.GetProperty("formDirty").GetBoolean());
        Assert.True(data.GetProperty("values")[0].GetProperty("isDirty").GetBoolean());
        Assert.Contains(supplied.ExposedCitations, citation => citation.NodeId == $"form-values:{pending.RequestId}");
    }

    [Fact]
    public async Task CrmDataTool_CurrentRecordIsForcedToSnapshotEntityAndOneRow()
    {
        var fixture = CreateFixture();
        var consented = new EvidenceToolSession(
            fixture.Store,
            fixture.Snapshot,
            fixture.Graph,
            dataAccessConsent: true);

        await consented.ExecuteAsync(
            "query_crm_data",
            """{"entity":"new_ticket","select":["statuscode"],"top":50,"current_record":true,"purpose":"读取当前记录状态"}""",
            CancellationToken.None);

        var pending = Assert.Single(consented.PendingDataRequests);
        Assert.True(pending.CurrentRecord);
        Assert.Equal(1, pending.Top);

        var rejected = await consented.ExecuteAsync(
            "query_crm_data",
            """{"entity":"account","select":["name"],"current_record":true,"purpose":"尝试读取其他实体"}""",
            CancellationToken.None);
        using var rejectedDocument = JsonDocument.Parse(rejected);
        Assert.False(rejectedDocument.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("当前记录查询", rejectedDocument.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task CustomApiTools_ResolveOnlyCatalogedApiAndReadItsImplementation()
    {
        var fixture = CreateFixture();
        var resolved = await fixture.Session.ExecuteAsync(
            "resolve_custom_api",
            """{"operation":"new_service","route":"CSSparePartInQuiry/CalculatePrice"}""",
            CancellationToken.None);
        using var resolvedDocument = JsonDocument.Parse(resolved);
        var api = Assert.Single(resolvedDocument.RootElement.GetProperty("customApis").EnumerateArray());
        var nodeId = api.GetProperty("nodeId").GetString();

        var implementation = await fixture.Session.ExecuteAsync(
            "read_custom_api_implementation",
            $$"""{"custom_api_node_id":"{{nodeId}}","search":"CSSparePartInQuiry/CalculatePrice"}""",
            CancellationToken.None);
        using var implementationDocument = JsonDocument.Parse(implementation);
        Assert.True(implementationDocument.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains(
            "CSSparePartInQuiry/CalculatePrice",
            implementationDocument.RootElement.GetProperty("excerpt").GetString(),
            StringComparison.Ordinal);

        var rejected = await fixture.Session.ExecuteAsync(
            "read_custom_api_implementation",
            """{"custom_api_node_id":"plugin-step:other"}""",
            CancellationToken.None);
        using var rejectedDocument = JsonDocument.Parse(rejected);
        Assert.False(rejectedDocument.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task RuntimeErrorTool_RequiresSuppliedObservedFailureAndCreatesCitation()
    {
        var fixture = CreateFixture();
        var session = new EvidenceToolSession(
            fixture.Store,
            fixture.Snapshot,
            fixture.Graph,
            runtimeDiagnostics:
            [
                new RuntimeDiagnosticEvidence(
                    "POST",
                    "/demo/api/data/v9.0/new_service",
                    400,
                    "{\"error\":{\"message\":\"价目表未配置\"}}",
                    DateTimeOffset.UtcNow)
            ]);
        Assert.Equal(8, session.GetToolDefinitions().Length);

        var result = await session.ExecuteAsync(
            "read_runtime_errors",
            """{"operation":"new_service"}""",
            CancellationToken.None);
        using var document = JsonDocument.Parse(result);
        var error = Assert.Single(document.RootElement.GetProperty("errors").EnumerateArray());
        Assert.Equal(400, error.GetProperty("status").GetInt32());
        Assert.Contains("价目表未配置", error.GetProperty("responseBody").GetString());
        Assert.Contains(session.ExposedCitations, citation => citation.NodeId.StartsWith("runtime-error:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecordedRuntimeTool_ReturnsChronologicalFrozenTimeline()
    {
        var fixture = CreateFixture();
        var capturedAt = DateTimeOffset.UtcNow;
        var session = new EvidenceToolSession(
            fixture.Store,
            fixture.Snapshot,
            fixture.Graph,
            runtimeRecording:
            [
                new RuntimeRecordingEvent(2, "http-error", "POST new_service 返回 400", "价目表未配置", capturedAt.AddSeconds(2), "POST", "/demo/api/data/v9.0/new_service", 400),
                new RuntimeRecordingEvent(1, "user-action", "点击“计算价格”", "元素：button", capturedAt.AddSeconds(1))
            ]);

        Assert.Contains(session.GetToolDefinitions(), definition =>
            JsonSerializer.Serialize(definition).Contains("read_recorded_runtime_events", StringComparison.Ordinal));
        var result = await session.ExecuteAsync(
            "read_recorded_runtime_events",
            "{}",
            CancellationToken.None);

        using var document = JsonDocument.Parse(result);
        var events = document.RootElement.GetProperty("events");
        Assert.Equal(2, events.GetArrayLength());
        Assert.Equal("user-action", events[0].GetProperty("kind").GetString());
        Assert.Equal("http-error", events[1].GetProperty("kind").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("errorCount").GetInt32());
        Assert.Contains(session.ExposedCitations, citation =>
            citation.NodeId.StartsWith("runtime-recording:", StringComparison.Ordinal));
        Assert.Contains(session.BuildAnalysisTrace(2, 10), step =>
            step.ToolName == "read_recorded_runtime_events" && step.Status == "completed");
    }

    [Fact]
    public async Task RecordedDataverseQueryTool_ReturnsEmptyQueryStructureAndCitation()
    {
        var fixture = CreateFixture();
        var capturedAt = DateTimeOffset.UtcNow;
        var details = """
            {"entitySet":"systemusers","queryOptions":{"$select":"fullname","$filter":"statecode eq <value> and territoryid eq <guid>"},"resultCount":0,"durationMs":87}
            """;
        var session = new EvidenceToolSession(
            fixture.Store,
            fixture.Snapshot,
            fixture.Graph,
            runtimeRecording:
            [
                new RuntimeRecordingEvent(1, "dataverse-query", "查询 systemusers，返回 0 条", details,
                    capturedAt, "GET", "/demo/api/data/v9.1/systemusers", 200)
            ]);

        Assert.Contains(session.GetToolDefinitions(), definition =>
            JsonSerializer.Serialize(definition).Contains("read_recorded_dataverse_queries", StringComparison.Ordinal));
        var result = await session.ExecuteAsync(
            "read_recorded_dataverse_queries",
            """{"search":"territoryid","empty_only":true}""",
            CancellationToken.None);

        using var document = JsonDocument.Parse(result);
        var query = Assert.Single(document.RootElement.GetProperty("queries").EnumerateArray());
        Assert.Equal(0, query.GetProperty("resultCount").GetInt32());
        Assert.Equal("systemusers", query.GetProperty("entitySet").GetString());
        Assert.Contains(session.ExposedCitations, citation =>
            citation.NodeId.StartsWith("runtime-query:", StringComparison.Ordinal));
        Assert.Contains(session.BuildAnalysisTrace(1, 10), step =>
            step.ToolName == "read_recorded_dataverse_queries" && step.Status == "completed");
    }

    private static Fixture CreateFixture()
    {
        var snapshotId = Guid.NewGuid();
        var context = new CrmPageContext(
            "https://crm.example.test/org",
            "org",
            "9.1",
            "v9.1",
            "entityrecord",
            "new_ticket",
            "11111111-2222-3333-4444-555555555555",
            "form-1",
            null,
            "工单");
        var source = """
            namespace Custom;
            public class TicketPlugin : IPlugin
            {
                private const string apiKey = "super-secret-value";
                private const string route = "CSSparePartInQuiry/CalculatePrice";
                public void Execute(IServiceProvider provider)
                {
                    Entity target = new Entity("new_ticket");
                    target["statuscode"] = 2;
                    service.Update(target);
                }
            }
            """;
        var sourceArtifact = new ArtifactUpload(
            ArtifactKind.DecompiledCSharp,
            "Custom.Plugin.decompiled.cs",
            "assembly-1",
            "1.0.0.0",
            "text/x-csharp; charset=utf-8",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(source)));
        var storedArtifact = new StoredArtifact(
            sourceArtifact.Kind,
            sourceArtifact.Name,
            sourceArtifact.ComponentId,
            sourceArtifact.Version,
            sourceArtifact.MediaType,
            new string('a', 64),
            Encoding.UTF8.GetByteCount(source),
            null);
        var snapshot = new StoredSnapshot(
            snapshotId,
            Guid.NewGuid(),
            context,
            [storedArtifact],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        var nodes = new EvidenceNode[]
        {
            Step("step:current", "new_ticket", "Update", "Custom.TicketPlugin"),
            Step("step:other", "account", "Update", "Custom.AccountPlugin"),
            new(
                "type:current", "PluginType", "Custom.TicketPlugin", "注册插件类型。", "plugin-catalog.json", null,
                EvidenceConfidence.Confirmed,
                new Dictionary<string, string> { ["TypeName"] = "Custom.TicketPlugin" }),
            new(
                "custom-api:new-service", "CustomApi", "new_service", "自定义 API new_service 路由到计算价格。", "plugin-catalog.json", null,
                EvidenceConfidence.Confirmed,
                new Dictionary<string, string>
                {
                    ["Operation"] = "new_service",
                    ["Route"] = "CSSparePartInQuiry/CalculatePrice",
                    ["PluginTypeId"] = "type:current"
                }),
            new(
                "csharp:type", "CSharpPluginType", "Custom.TicketPlugin", "反编译插件类型。", sourceArtifact.Name, "line 2: class Custom.TicketPlugin",
                EvidenceConfidence.Confirmed,
                new Dictionary<string, string> { ["TypeName"] = "Custom.TicketPlugin", ["AssemblyId"] = "assembly-1" }),
            new(
                "execute:current", "CSharpExecuteMethod", "Custom.TicketPlugin.Execute", "插件入口。", sourceArtifact.Name, "line 5: Execute",
                EvidenceConfidence.Confirmed,
                new Dictionary<string, string> { ["TypeName"] = "Custom.TicketPlugin" }),
            new(
                "behavior:update", "CSharpPluginBehavior", "OrganizationServiceCall", "更新当前工单状态。", sourceArtifact.Name, "line 9: OrganizationServiceCall",
                EvidenceConfidence.Confirmed,
                new Dictionary<string, string> { ["Operation"] = "Update", ["Entity"] = "new_ticket", ["Fields"] = "statuscode" }),
            new(
                "method:legacy", "ManagedMethod", "Newtonsoft.Json.Serialize", "DLL 内部方法目录。", "Custom.Plugin.dll", null,
                EvidenceConfidence.Confirmed)
        };
        var edges = new EvidenceEdge[]
        {
            new("step:current", "type:current", "handled-by", EvidenceConfidence.Confirmed),
            new("custom-api:new-service", "type:current", "implemented-by", EvidenceConfidence.Confirmed),
            new("type:current", "csharp:type", "matches-decompiled-type", EvidenceConfidence.Confirmed),
            new("csharp:type", "execute:current", "declares-execute", EvidenceConfidence.Confirmed),
            new("execute:current", "behavior:update", "performs", EvidenceConfidence.Confirmed)
        };
        var store = new InMemoryStore(snapshot, sourceArtifact);
        var graph = new EvidenceGraph(nodes, edges, []);
        return new Fixture(snapshot, graph, store, new EvidenceToolSession(store, snapshot, graph));
    }

    private static EvidenceNode Step(string id, string entity, string message, string pluginType) => new(
        id,
        "PluginStep",
        $"{entity} {message}",
        $"当前步骤在 {entity} {message} 时执行。",
        "plugin-catalog.json",
        id,
        EvidenceConfidence.Confirmed,
        new Dictionary<string, string>
        {
            ["Entity"] = entity,
            ["Message"] = message,
            ["PluginType"] = pluginType,
            ["Enabled"] = "True"
        });

    private sealed record Fixture(
        StoredSnapshot Snapshot,
        EvidenceGraph Graph,
        InMemoryStore Store,
        EvidenceToolSession Session);

    private sealed class InMemoryStore(StoredSnapshot snapshot, ArtifactUpload artifact) : IAnalysisStore
    {
        public List<(Guid SnapshotId, ArtifactKind Kind, string Name)> ArtifactReads { get; } = [];
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<StoredArtifact> StoreArtifactAsync(PreparedArtifact value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveSnapshotAsync(StoredSnapshot value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<StoredSnapshot?> GetSnapshotAsync(Guid snapshotId, CancellationToken cancellationToken = default) => Task.FromResult<StoredSnapshot?>(snapshot);
        public Task<SnapshotUpload?> LoadSnapshotUploadAsync(Guid snapshotId, CancellationToken cancellationToken = default) => Task.FromResult<SnapshotUpload?>(null);
        public Task<ArtifactUpload?> LoadArtifactAsync(Guid snapshotId, ArtifactKind kind, string name, CancellationToken cancellationToken = default)
        {
            ArtifactReads.Add((snapshotId, kind, name));
            return Task.FromResult<ArtifactUpload?>(
                snapshotId == snapshot.SnapshotId && kind == artifact.Kind &&
                string.Equals(name, artifact.Name, StringComparison.OrdinalIgnoreCase)
                    ? artifact
                    : null);
        }
        public Task SaveJobAsync(AnalysisJob job, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AnalysisJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default) => Task.FromResult<AnalysisJob?>(null);
        public Task<IReadOnlyList<AnalysisJob>> GetRecoverableJobsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AnalysisJob>>([]);
        public Task SaveAnalysisAsync(AnalysisResult result, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AnalysisResult?> GetAnalysisAsync(Guid snapshotId, CancellationToken cancellationToken = default) => Task.FromResult<AnalysisResult?>(null);
        public Task<bool> CheckWritableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
