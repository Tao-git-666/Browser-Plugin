using System.Text.Json;
using CrmLogicLens.Api.Services;
using CrmLogicLens.Api.Storage;
using CrmLogicLens.Core;

namespace CrmLogicLens.Core.Tests;

public sealed class DiagnosticSkillCatalogTests
{
    [Theory]
    [InlineData("提交按钮点击后报错", "button-execution-error")]
    [InlineData("命令栏按钮灰色不能点击", "button-not-visible-or-disabled")]
    [InlineData("new_service 接口500", "custom-api-error")]
    [InlineData("派工页面没有可用人员", "custom-page-empty-list")]
    [InlineData("保存后又变回去了", "data-not-updated")]
    [InlineData("当前审批按钮有什么用", "explain-current-logic")]
    [InlineData("金额字段不能编辑", "field-not-editable")]
    [InlineData("折扣字段不显示", "field-not-visible")]
    [InlineData("Update 插件没触发", "plugin-not-triggered")]
    [InlineData("保存报错", "save-failed")]
    [InlineData("身份证号的填写逻辑是什么", "field-value-rules")]
    [InlineData("证件号码填写格式", "field-value-rules")]
    [InlineData("金额自动赋值是怎么来的", "field-value-rules")]
    [InlineData("服务类型为维修时隐藏哪些字段", "field-not-visible")]
    [InlineData("身份证号保存失败", "save-failed")]
    [InlineData("点按钮没反应", "button-execution-error")]
    [InlineData("提交按钮一直转圈", "button-execution-error")]
    [InlineData("按钮重复提交了两次", "button-execution-error")]
    [InlineData("按钮打开弹窗失败", "button-execution-error")]
    [InlineData("按钮置灰了", "button-not-visible-or-disabled")]
    [InlineData("命令栏按钮被禁用", "button-not-visible-or-disabled")]
    [InlineData("按钮时有时无", "button-not-visible-or-disabled")]
    [InlineData("为什么选中记录后按钮才可用", "button-not-visible-or-disabled")]
    [InlineData("自定义接口403", "custom-api-error")]
    [InlineData("new_service 接口404", "custom-api-error")]
    [InlineData("派工接口超时", "custom-api-error")]
    [InlineData("Action执行失败", "custom-api-error")]
    [InlineData("HTTP成功但接口返回业务错误", "custom-api-error")]
    [InlineData("人员查找无结果", "custom-page-empty-list")]
    [InlineData("子网格没有数据", "custom-page-empty-list")]
    [InlineData("列表有数据但不显示", "custom-page-empty-list")]
    [InlineData("下拉选项为空", "custom-page-empty-list")]
    [InlineData("金额保存后值丢失", "data-not-updated")]
    [InlineData("主表保存成功但关联数据未更新", "data-not-updated")]
    [InlineData("刷新后才显示新状态", "data-not-updated")]
    [InlineData("异步更新没完成", "data-not-updated")]
    [InlineData("加载窗体的执行顺序", "explain-current-logic")]
    [InlineData("审批影响哪些数据", "explain-current-logic")]
    [InlineData("金额字段被锁定", "field-not-editable")]
    [InlineData("字段置灰", "field-not-editable")]
    [InlineData("改状态后字段突然只读", "field-not-editable")]
    [InlineData("表头不能编辑", "field-not-editable")]
    [InlineData("加载后字段时隐时现", "field-not-visible")]
    [InlineData("字段被隐藏", "field-not-visible")]
    [InlineData("整个页签不见了", "field-not-visible")]
    [InlineData("详细信息节被隐藏", "field-not-visible")]
    [InlineData("证件号必填规则是什么", "field-value-rules")]
    [InlineData("手机号码被清空", "field-value-rules")]
    [InlineData("金额精度如何控制", "field-value-rules")]
    [InlineData("日期自动变化", "field-value-rules")]
    [InlineData("输入后被清空", "field-value-rules")]
    [InlineData("为什么插件偶尔不执行", "plugin-not-triggered")]
    [InlineData("插件提前返回的条件", "plugin-not-triggered")]
    [InlineData("插件异步任务失败", "plugin-not-triggered")]
    [InlineData("更新字段不触发插件", "plugin-not-triggered")]
    [InlineData("自动保存失败", "save-failed")]
    [InlineData("保存并关闭失败", "save-failed")]
    [InlineData("保存一直转圈", "save-failed")]
    [InlineData("保存提示重复记录", "save-failed")]
    public void ProductionSkills_RouteRepresentativeQuestions(string question, string expectedSkillId)
    {
        var catalog = LoadProductionCatalog();

        var match = Assert.Single(catalog.Search(question, 1));

        Assert.Equal(expectedSkillId, match.Skill.Id);
    }

    [Fact]
    public void ProductionSkills_ReverseWriterRequiresAnEnvironmentLibrary()
    {
        var catalog = LoadProductionCatalog();
        const string question = "设备档案没有生成，是哪个插件创建，触发条件不对吗";
        Assert.DoesNotContain(catalog.Search(question, 3), match => match.Skill.Id == "reverse-entity-writer");
        var match = Assert.Single(catalog.Search(question, 1,
            new HashSet<string>(StringComparer.Ordinal) { "environment-code-library" }));
        Assert.Equal("reverse-entity-writer", match.Skill.Id);
        Assert.Contains("search_environment_code", match.Skill.RequiredTools);
    }

    [Theory]
    [InlineData("录制的脚本异常", "runtime-recording", "recorded-runtime-error")]
    [InlineData("录制后没有错误", "runtime-recording", "recorded-runtime-error")]
    [InlineData("录制期间报错", "runtime-recording", "recorded-runtime-error")]
    [InlineData("关联记录没有生成", "environment-code-library", "reverse-entity-writer")]
    [InlineData("数据是谁生成的", "environment-code-library", "reverse-entity-writer")]
    [InlineData("反查写入来源", "environment-code-library", "reverse-entity-writer")]
    public void ProductionSkills_RouteConditionalScenariosOnlyWithRequiredSignal(
        string question, string signal, string expectedSkillId)
    {
        var catalog = LoadProductionCatalog();

        Assert.DoesNotContain(catalog.Search(question, 5), match => match.Skill.Id == expectedSkillId);
        Assert.Equal(expectedSkillId, Assert.Single(catalog.Search(question, 1,
            new HashSet<string>(StringComparer.Ordinal) { signal })).Skill.Id);
    }

    [Theory]
    [InlineData("设备档案有什么用", "explain-current-logic")]
    [InlineData("设备档案的金额字段不能编辑", "field-not-editable")]
    [InlineData("设备档案的填写规则", "field-value-rules")]
    [InlineData("审批按钮有什么用", "explain-current-logic")]
    [InlineData("字段不显示", "field-not-visible")]
    public void ProductionSkills_DoNotRouteUnrelatedQuestionsToSignalSpecificSkills(
        string question, string expectedSkillId)
    {
        var catalog = LoadProductionCatalog();
        var signals = new HashSet<string>(StringComparer.Ordinal)
        {
            "runtime-recording", "recorded-dataverse-queries", "runtime-errors",
            "crm-data-access", "environment-code-library"
        };

        Assert.Equal(expectedSkillId, Assert.Single(catalog.Search(question, 1, signals)).Skill.Id);
    }

    [Theory]
    [InlineData("字段只读", "field-not-editable")]
    [InlineData("保存被阻止", "save-failed")]
    [InlineData("保存后值丢失", "data-not-updated")]
    [InlineData("按钮执行失败", "button-execution-error")]
    [InlineData("API报错", "custom-api-error")]
    public async Task ProductionSkills_CanReportStaticEvidenceWithoutUnrelatedOrUnresolvedTools(
        string question, string expectedSkillId)
    {
        var snapshot = CreateSkillSnapshot();
        var session = new EvidenceToolSession(
            new EmptyStore(snapshot), snapshot,
            new EvidenceGraph(
                [new EvidenceNode("field:amount", "FormControl", "金额", "当前窗体金额控件。",
                    "form.xml", "control amount", EvidenceConfidence.Confirmed)], [], []),
            dataAccessConsent: true,
            diagnosticSkills: LoadProductionCatalog());

        // Select through the same discovery tools the model uses, including ambiguous cases.
        await session.ExecuteAsync("search_diagnostic_skills",
            JsonSerializer.Serialize(new { query = question, limit = 5 }), CancellationToken.None);
        await session.ExecuteAsync("read_diagnostic_skill",
            JsonSerializer.Serialize(new { skill_id = expectedSkillId }), CancellationToken.None);
        Assert.Equal(expectedSkillId, session.GetDiagnosticSkillReadiness().SkillId);
        Assert.False(session.GetDiagnosticSkillReadiness().Ready);

        await session.ExecuteAsync("find_business_logic", """{"query":"金额"}""", CancellationToken.None);
        await session.ExecuteAsync("trace_evidence", """{"node_id":"field:amount"}""", CancellationToken.None);
        await session.ExecuteAsync("check_diagnostic_progress", "{}", CancellationToken.None);

        Assert.True(session.GetDiagnosticSkillReadiness().Ready);
        Assert.Empty(session.PendingDataRequests);
        Assert.Empty(session.PendingFormValueRequests);
    }

    [Fact]
    public async Task ProductionApiSkill_RequiresImplementationAfterSuccessfulResolution()
    {
        var snapshot = CreateSkillSnapshot();
        var graph = new EvidenceGraph(
            [new EvidenceNode("custom-api:submit", "CustomApi", "提交工单", "提交工单路由。",
                "plugin-catalog.json", "custom api new_service", EvidenceConfidence.Confirmed,
                new Dictionary<string, string> { ["Operation"] = "new_service", ["Route"] = "Ticket/Submit" })],
            [], []);
        var session = new EvidenceToolSession(new EmptyStore(snapshot), snapshot, graph,
            diagnosticSkills: LoadProductionCatalog());
        await session.PrepareDiagnosticSkillAsync("new_service 接口500", CancellationToken.None);
        Assert.Equal("custom-api-error", session.GetDiagnosticSkillReadiness().SkillId);
        Assert.DoesNotContain("read_custom_api_implementation", session.GetDiagnosticSkillReadiness().MissingTools);

        await session.ExecuteAsync("resolve_custom_api",
            """{"operation":"new_service","route":"Ticket/Submit"}""", CancellationToken.None);

        Assert.Contains("read_custom_api_implementation", session.GetDiagnosticSkillReadiness().MissingTools);
        Assert.False(session.GetDiagnosticSkillReadiness().Ready);
    }

    [Fact]
    public void ProductionSkills_KeepRecordingDependenciesConditional()
    {
        var catalog = LoadProductionCatalog();
        var customPage = Assert.Single(catalog.Skills, skill => skill.Id == "custom-page-empty-list");
        var recordedError = Assert.Single(catalog.Skills, skill => skill.Id == "recorded-runtime-error");

        Assert.DoesNotContain("read_recorded_dataverse_queries", customPage.RequiredTools);
        Assert.Contains("read_recorded_dataverse_queries", customPage.RequiredWhenAvailableTools);
        Assert.Contains("runtime-recording", recordedError.RequiredSignals);
        Assert.DoesNotContain(catalog.Search("录制期间报错", 3), match => match.Skill.Id == recordedError.Id);
        Assert.Equal(
            recordedError.Id,
            Assert.Single(catalog.Search(
                "录制期间报错",
                1,
                new HashSet<string>(StringComparer.Ordinal) { "runtime-recording" })).Skill.Id);
    }

    [Fact]
    public void ProductionSkills_UseFallbackWhenOnlyGenericDescriptionWordsMatch()
    {
        var catalog = LoadProductionCatalog();

        var match = Assert.Single(catalog.Search("页面出现未知错误", 1));

        Assert.Equal("explain-current-logic", match.Skill.Id);
    }

    [Fact]
    public async Task ToolSession_DoesNotAutoSelectWhenTopSkillsAreAmbiguous()
    {
        const string question = "点击后报错，同时保存失败";
        var catalog = LoadProductionCatalog();
        var matches = catalog.Search(question, 3);
        Assert.True(
            DiagnosticSkillCatalog.HasAmbiguousTopMatch(matches),
            string.Join(", ", matches.Select(match => $"{match.Skill.Id}={match.Score}")));

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
        var session = new EvidenceToolSession(
            new EmptyStore(snapshot),
            snapshot,
            new EvidenceGraph([], [], []),
            diagnosticSkills: catalog);

        await session.PrepareDiagnosticSkillAsync(question, CancellationToken.None);

        Assert.Equal(
            "search_diagnostic_skills",
            Assert.Single(session.GetDiagnosticSkillReadiness().MissingTools));

        var response = await session.ExecuteAsync(
            "search_diagnostic_skills",
            JsonSerializer.Serialize(new { query = question, limit = 1 }),
            CancellationToken.None);
        using var document = JsonDocument.Parse(response);
        Assert.True(document.RootElement.GetProperty("ambiguous").GetBoolean());
        Assert.Equal(2, document.RootElement.GetProperty("matches").GetArrayLength());

        var rejected = await session.ExecuteAsync(
            "read_diagnostic_skill",
            """{"skill_id":"explain-current-logic"}""",
            CancellationToken.None);
        using var rejectedDocument = JsonDocument.Parse(rejected);
        Assert.False(rejectedDocument.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains(
            "最近一次",
            rejectedDocument.RootElement.GetProperty("error").GetString(),
            StringComparison.Ordinal);

        var contextJson = JsonSerializer.Serialize(session.BuildConversationContext(question));
        using var contextDocument = JsonDocument.Parse(contextJson);
        Assert.Contains(
            "服务器未自动选择 Skill",
            contextDocument.RootElement
                .GetProperty("diagnosticSkills")
                .GetProperty("rule")
                .GetString(),
            StringComparison.Ordinal);
    }

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
                requires-signals: runtime-errors
                ---
                读取实际错误并对照 API 实现。
                """);

            var catalog = DiagnosticSkillCatalog.LoadFromDirectory(root);
            var withoutRuntimeError = Assert.Single(catalog.Search("new_service 接口400是什么原因", 1));
            Assert.Equal("general", withoutRuntimeError.Skill.Id);
            var match = Assert.Single(catalog.Search(
                "new_service 接口400是什么原因",
                1,
                new HashSet<string>(StringComparer.Ordinal) { "runtime-errors" }));

            Assert.Equal("custom-api-error", match.Skill.Id);
            Assert.Equal("1.2", match.Skill.Version);
            Assert.Contains("resolve_custom_api", match.Skill.RequiredTools);
            Assert.Contains("read_runtime_errors", match.Skill.RequiredWhenAvailableTools);
            Assert.Contains("runtime-errors", match.Skill.RequiredSignals);
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
                ["read_runtime_errors"],
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

    [Fact]
    public async Task ToolSession_RejectsSkillWhenRequiredRuntimeSignalIsMissing()
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
        var catalog = new DiagnosticSkillCatalog(
        [
            new DiagnosticSkill(
                "recorded-runtime-error",
                "排查已录制错误。",
                "1.1",
                ["录制报错"],
                ["read_recorded_runtime_events"],
                [],
                ["runtime-recording"],
                "先读取录制事件。",
                false,
                "test"),
            new DiagnosticSkill(
                "explain-current-logic",
                "一般问题。",
                "1.0",
                ["业务逻辑"],
                ["find_business_logic"],
                [],
                [],
                "查找相关证据。",
                true,
                "test")
        ]);
        var session = new EvidenceToolSession(
            new EmptyStore(snapshot),
            snapshot,
            new EvidenceGraph([], [], []),
            diagnosticSkills: catalog);

        await session.ExecuteAsync(
            "search_diagnostic_skills",
            """{"query":"录制报错"}""",
            CancellationToken.None);
        var result = await session.ExecuteAsync(
            "read_diagnostic_skill",
            """{"skill_id":"recorded-runtime-error"}""",
            CancellationToken.None);

        using var document = JsonDocument.Parse(result);
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains(
            "runtime-recording",
            document.RootElement.GetProperty("error").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolSession_CompletesChecklistButPreservesEmptyAndFailedToolEvidenceGaps()
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
        var catalog = new DiagnosticSkillCatalog(
        [
            new DiagnosticSkill(
                "field-not-visible",
                "排查字段不显示。",
                "1.0",
                ["字段不显示"],
                ["find_business_logic", "trace_evidence"],
                [],
                [],
                "先定位字段，再追踪证据。",
                false,
                "test")
        ]);
        var session = new EvidenceToolSession(
            new EmptyStore(snapshot),
            snapshot,
            new EvidenceGraph([], [], []),
            diagnosticSkills: catalog);

        await session.PrepareDiagnosticSkillAsync("状态字段不显示", CancellationToken.None);
        await session.ExecuteAsync(
            "find_business_logic",
            """{"query":"状态字段"}""",
            CancellationToken.None);
        await session.ExecuteAsync(
            "trace_evidence",
            """{"node_id":"field:missing","depth":1}""",
            CancellationToken.None);

        var beforeCheck = session.GetDiagnosticSkillReadiness();
        Assert.Equal("check_diagnostic_progress", Assert.Single(beforeCheck.MissingTools));
        Assert.Equal(2, beforeCheck.EvidenceGaps.Count);
        Assert.Contains(beforeCheck.EvidenceGaps, gap => gap.StartsWith("find_business_logic:", StringComparison.Ordinal));
        Assert.Contains(beforeCheck.EvidenceGaps, gap => gap.StartsWith("trace_evidence:", StringComparison.Ordinal));

        var response = await session.ExecuteAsync(
            "check_diagnostic_progress",
            "{}",
            CancellationToken.None);
        using var document = JsonDocument.Parse(response);
        Assert.True(document.RootElement.GetProperty("ready").GetBoolean());
        Assert.Equal(2, document.RootElement.GetProperty("evidenceGaps").GetArrayLength());
        Assert.True(session.GetDiagnosticSkillReadiness().Ready);
        Assert.Equal(2, session.GetDiagnosticSkillReadiness().EvidenceGaps.Count);

        var finalContextJson = JsonSerializer.Serialize(session.BuildFinalAnswerContext("状态字段不显示", 8_192));
        using var finalContext = JsonDocument.Parse(finalContextJson);
        Assert.Equal(
            2,
            finalContext.RootElement
                .GetProperty("diagnosticSkill")
                .GetProperty("evidenceGaps")
                .GetArrayLength());
    }

    private static StoredSnapshot CreateSkillSnapshot() => new(
        Guid.NewGuid(), Guid.NewGuid(),
        new CrmPageContext("https://crm.example.test/org", "org", "9.1", "v9.1", "entityrecord",
            "new_ticket", null, "form-1", null, "工单"),
        [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static void WriteSkill(string root, string id, string content)
    {
        var directory = Path.Combine(root, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "SKILL.md"), content);
    }

    private static DiagnosticSkillCatalog LoadProductionCatalog()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CrmLogicLens.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return DiagnosticSkillCatalog.LoadFromDirectory(Path.Combine(
            directory.FullName,
            "src",
            "CrmLogicLens.Api",
            "DiagnosticSkills"));
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
