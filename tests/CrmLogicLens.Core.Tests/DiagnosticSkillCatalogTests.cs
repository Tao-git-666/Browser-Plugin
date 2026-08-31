using System.Text.Json;
using CrmLogicLens.Api.Services;
using CrmLogicLens.Api.Storage;
using CrmLogicLens.Core;

namespace CrmLogicLens.Core.Tests;

public sealed class DiagnosticSkillCatalogTests
{
    [Fact]
    public void LoadFromDirectory_ParsesSkillsAndPrefersSpecificTrigger()
    {
        var root = Path.Combine(Path.GetTempPath(), $"crm-logic-lens-skills-{Guid.NewGuid():N}");
        try
        {
            WriteSkill(root, "general", """
                ---
                name: general
                description: 一般问题。
                version: 1.0
                triggers: 业务逻辑
                required-tools: find_business_logic
                fallback: true
                ---
                查找相关证据后回答。
                """);
            WriteSkill(root, "custom-api-error", """
                ---
                name: custom-api-error
                description: 排查自定义 API 失败。
                version: 1.2
                triggers: 自定义 API,接口400,new_service
                required-tools: find_business_logic,resolve_custom_api,read_custom_api_implementation
                required-when-available: read_runtime_errors
                ---
                读取实际错误并对照 API 实现。
                """);

            var catalog = DiagnosticSkillCatalog.LoadFromDirectory(root);
            var match = Assert.Single(catalog.Search("new_service 接口400是什么原因", 1));

            Assert.Equal("custom-api-error", match.Skill.Id);
            Assert.Equal("1.2", match.Skill.Version);
            Assert.Contains("resolve_custom_api", match.Skill.RequiredTools);
            Assert.Contains("read_runtime_errors", match.Skill.RequiredWhenAvailableTools);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ToolSession_RequiresSkillSelectionEvidenceToolsAndFinalProgressCheck()
    {
        var snapshotId = Guid.NewGuid();
        var snapshot = new StoredSnapshot(
            snapshotId,
            Guid.NewGuid(),
            new CrmPageContext(
                "https://crm.example.test/org", "org", "9.1", "v9.1", "entityrecord",
                "new_ticket", null, "form-1", null, "工单"),
            [],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        var graph = new EvidenceGraph(
        [
            new EvidenceNode(
                "field:status", "FormControl", "处理状态", "当前窗体上的处理状态字段。",
                "form.xml", "control status", EvidenceConfidence.Confirmed)
        ],
        [],
        []);
        var catalog = new DiagnosticSkillCatalog(
        [
            new DiagnosticSkill(
                "field-not-visible",
                "排查字段不显示。",
                "1.0",
                ["字段不显示"],
                ["find_business_logic", "trace_evidence"],
                [],
                "先确认字段控件，再检查可见性。",
                false,
                "test")
        ]);
        var session = new EvidenceToolSession(
            new EmptyStore(snapshot),
            snapshot,
            graph,
            diagnosticSkills: catalog);

        Assert.Equal(10, session.GetToolDefinitions().Length);
        Assert.Equal("search_diagnostic_skills", Assert.Single(session.GetDiagnosticSkillReadiness().MissingTools));

        var searchJson = await session.ExecuteAsync(
            "search_diagnostic_skills",
            """{"query":"处理状态字段为什么不显示"}""",
            CancellationToken.None);
        using (var search = JsonDocument.Parse(searchJson))
        {
            Assert.Equal(
                "field-not-visible",
                search.RootElement.GetProperty("matches")[0].GetProperty("skillId").GetString());
        }
        await session.ExecuteAsync(
            "read_diagnostic_skill",
            """{"skill_id":"field-not-visible"}""",
            CancellationToken.None);
        Assert.Contains("find_business_logic", session.GetDiagnosticSkillReadiness().MissingTools);

        await session.ExecuteAsync(
            "find_business_logic",
            """{"query":"处理状态"}""",
            CancellationToken.None);
        await session.ExecuteAsync(
            "trace_evidence",
            """{"node_id":"field:status","depth":1}""",
            CancellationToken.None);
        Assert.Equal("check_diagnostic_progress", Assert.Single(session.GetDiagnosticSkillReadiness().MissingTools));

        var progressJson = await session.ExecuteAsync(
            "check_diagnostic_progress",
            "{}",
            CancellationToken.None);
        using var progress = JsonDocument.Parse(progressJson);
        Assert.True(progress.RootElement.GetProperty("ready").GetBoolean());
        Assert.True(session.GetDiagnosticSkillReadiness().Ready);
        var trace = session.BuildAnalysisTrace(1, 10);
        Assert.Contains(trace, step => step.Title == "采用诊断 Skill");
        Assert.Contains(trace, step => step.Title == "检查 Skill 必查项" && step.Status == "completed");
    }

    [Fact]
    public async Task ToolSession_RecoversCustomApiImplementationWhenModelStopsEarly()
    {
        var snapshotId = Guid.NewGuid();
        var snapshot = new StoredSnapshot(
            snapshotId,
            Guid.NewGuid(),
            new CrmPageContext(
                "https://crm.example.test/org", "org", "9.1", "v9.1", "entityrecord",
                "new_ticket", null, "form-1", null, "工单"),
            [],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        var graph = new EvidenceGraph(
        [
            new EvidenceNode(
                "custom-api:submit", "CustomApi", "提交工单", "当前按钮调用提交工单路由。",
                "plugin-catalog.json", "custom api new_service", EvidenceConfidence.Confirmed,
                new Dictionary<string, string>
                {
                    ["Operation"] = "new_service",
                    ["Route"] = "Ticket/Submit"
                })
        ],
        [],
        []);
        var catalog = new DiagnosticSkillCatalog(
        [
            new DiagnosticSkill(
                "explain-current-logic",
                "解释按钮业务逻辑。",
                "1.0",
                ["会发生什么"],
                ["find_business_logic", "resolve_custom_api"],
                [],
                "解析自定义 API 后读取实现。",
                true,
                "test")
        ]);
        var session = new EvidenceToolSession(
            new EmptyStore(snapshot),
            snapshot,
            graph,
            diagnosticSkills: catalog);

        await session.PrepareDiagnosticSkillAsync("点击提交后会发生什么", CancellationToken.None);
        await session.ExecuteAsync(
            "find_business_logic",
            """{"query":"提交工单"}""",
            CancellationToken.None);
        await session.ExecuteAsync(
            "resolve_custom_api",
            """{"operation":"new_service","route":"Ticket/Submit"}""",
            CancellationToken.None);

        var beforeRecovery = session.GetDiagnosticSkillReadiness();
        Assert.Contains("read_custom_api_implementation", beforeRecovery.MissingTools);

        Assert.True(await session.TryCompleteDeterministicDiagnosticRecoveryAsync(
            beforeRecovery,
            CancellationToken.None));
        Assert.True(session.GetDiagnosticSkillReadiness().Ready);
        Assert.Contains(
            session.BuildAnalysisTrace(1, 10),
            step => step.ToolName == "read_custom_api_implementation");
    }

    private static void WriteSkill(string root, string id, string content)
    {
        var directory = Path.Combine(root, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "SKILL.md"), content);
    }

    private sealed class EmptyStore(StoredSnapshot snapshot) : IAnalysisStore
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
