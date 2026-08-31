using System.Text;

namespace CrmLogicLens.Core;

/// <summary>
/// Deterministic, evidence-only Chinese answer generation. It does not invent missing
/// behavior and every factual sentence includes one or more graph citations.
/// </summary>
public sealed class EvidenceAnswerService
{
    private const int MaxPrimaryNodes = 12;
    private const int MaxExpandedNodes = 20;

    public ChatResponse Answer(ChatRequest request, EvidenceGraph graph)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(graph);
        if (string.IsNullOrWhiteSpace(request.Question))
        {
            return new ChatResponse(
                "请先说明你想了解的窗体、字段、按钮或插件逻辑。",
                EvidenceConfidence.Unknown,
                [],
                ["问题为空，无法从证据图检索答案。"]);
        }

        var scored = graph.Nodes
            .Where(node => !IsTechnicalInventoryKind(node.Kind))
            .Select(node => (Node: node, Score: Score(node, request.Question, request.FocusComponentId)))
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => KindPriority(item.Node.Kind))
            .ThenBy(item => item.Node.Label, StringComparer.OrdinalIgnoreCase)
            .Take(MaxPrimaryNodes)
            .Select(item => item.Node)
            .ToList();

        if (scored.Count == 0 && IsBroadLogicQuestion(request.Question))
        {
            scored.AddRange(graph.Nodes
                .Where(node => IsBusinessLogicKind(node.Kind))
                .OrderBy(node => KindPriority(node.Kind))
                .Take(MaxPrimaryNodes));
        }

        if (scored.Count == 0)
        {
            return new ChatResponse(
                "当前快照中没有找到能直接回答这个问题的证据。请尝试指出字段逻辑名、按钮名称、JavaScript 函数名或插件步骤名。",
                EvidenceConfidence.Unknown,
                [],
                BuildUnknowns(request.Question, graph, []));
        }

        var selectedIds = scored.Select(node => node.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        ExpandConnectedEvidence(graph, scored, selectedIds);
        var selectedById = scored.ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
        var relevantEdges = graph.Edges.Where(edge =>
                selectedById.ContainsKey(edge.SourceId) && selectedById.ContainsKey(edge.TargetId))
            .OrderBy(edge => EdgePriority(edge.Relation))
            .ThenBy(edge => edge.SourceId, StringComparer.Ordinal)
            .Take(12)
            .ToArray();

        var citations = new Dictionary<string, EvidenceCitation>(StringComparer.OrdinalIgnoreCase);
        var answer = new StringBuilder();
        answer.AppendLine("根据当前快照的静态配置和代码，可以得到以下结论：");
        answer.AppendLine();
        foreach (var node in scored
                     .OrderBy(node => KindPriority(node.Kind))
                     .ThenBy(node => node.Label, StringComparer.OrdinalIgnoreCase)
                     .Take(MaxExpandedNodes))
        {
            answer.Append("- ");
            if (node.Confidence == EvidenceConfidence.Inferred)
            {
                answer.Append("推断：");
            }

            answer.Append(EnsureTerminalPunctuation(node.Summary));
            answer.Append(' ');
            answer.Append(Cite(node));
            answer.AppendLine();
            AddCitation(citations, node);
        }

        var relationshipLines = BuildRelationshipLines(relevantEdges, selectedById, citations).ToArray();
        if (relationshipLines.Length > 0)
        {
            answer.AppendLine();
            answer.AppendLine("调用关系：");
            answer.AppendLine();
            foreach (var line in relationshipLines)
            {
                answer.Append("- ");
                answer.AppendLine(line);
            }
        }

        var unknowns = BuildUnknowns(request.Question, graph, scored);
        if (unknowns.Count > 0)
        {
            answer.AppendLine();
            answer.Append("边界：");
            answer.Append(string.Join("；", unknowns.Take(3)));
            answer.Append('。');
        }

        var confidence = relevantEdges.Any(edge => edge.Confidence == EvidenceConfidence.Inferred) ||
                         scored.Any(node => node.Confidence == EvidenceConfidence.Inferred)
            ? EvidenceConfidence.Inferred
            : EvidenceConfidence.Confirmed;

        return new ChatResponse(answer.ToString().Trim(), confidence, citations.Values.ToArray(), unknowns);
    }

    private static int Score(EvidenceNode node, string question, string? focusComponentId)
    {
        var normalizedQuestion = question.Trim().ToLowerInvariant();
        var searchable = BuildSearchableText(node);
        var score = 0;

        if (!string.IsNullOrWhiteSpace(focusComponentId) &&
            (node.Id.Contains(focusComponentId, StringComparison.OrdinalIgnoreCase) ||
             node.Properties?.Values.Any(value =>
                 SameNormalizedComponent(value, focusComponentId)) == true))
        {
            score += 120;
        }

        if (node.Label.Length >= 2 && normalizedQuestion.Contains(node.Label, StringComparison.OrdinalIgnoreCase))
        {
            score += 35;
        }

        if (node.Properties is not null)
        {
            foreach (var value in node.Properties.Values.Where(value => value.Length is >= 2 and <= 160))
            {
                if (normalizedQuestion.Contains(value, StringComparison.OrdinalIgnoreCase))
                {
                    score += 22;
                }
            }
        }

        foreach (var token in Tokenize(normalizedQuestion))
        {
            if (token.Length >= 2 && searchable.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                score += token.All(char.IsAsciiLetterOrDigit) ? 8 : 5;
            }
        }

        score += CategoryScore(node.Kind, normalizedQuestion);
        if (node.Confidence == EvidenceConfidence.Confirmed && score > 0)
        {
            score++;
        }

        return score;
    }

    private static int CategoryScore(string kind, string question)
    {
        var buttonQuestion = ContainsAny(question, "按钮", "ribbon", "命令", "command");
        var pluginQuestion = ContainsAny(question, "插件", "plugin", "dll", "程序集", "反编译");
        var fieldQuestion = ContainsAny(question, "字段", "必填", "可见", "禁用", "读取", "写入", "赋值");
        var formQuestion = ContainsAny(question, "窗体", "onload", "onsave", "onchange", "保存", "加载");
        var apiQuestion = ContainsAny(question, "webapi", "api", "创建", "更新", "删除", "查询", "action", "操作");
        var ruleQuestion = ContainsAny(question, "规则", "条件", "显示", "启用");
        var score = 0;
        if (buttonQuestion && kind is "RibbonButton" or "RibbonCommand" or "RibbonJavaScriptAction" or "RibbonRule") score += 18;
        if (pluginQuestion && kind is "PluginStep" or "PluginType" or "PluginAssembly" or "CSharpPluginType" or "CSharpExecuteMethod" or "CSharpPluginBehavior" or "DecompiledPluginExcerpt") score += 18;
        if (fieldQuestion && kind is "Field" or "FieldMetadata" or "OptionMetadata" or "JavaScriptBehavior") score += 14;
        if (formQuestion && kind is "Form" or "FormEvent" or "FormHandler" or "JavaScriptFunction" or "JavaScriptBehavior") score += 14;
        if (apiQuestion && kind is "JavaScriptBehavior" or "PluginStep" or "PluginMessage") score += 14;
        if (ruleQuestion && kind is "RibbonRule" or "JavaScriptBehavior" or "FormHandler") score += 12;
        if (IsBroadLogicQuestion(question) && IsBusinessLogicKind(kind)) score += 8;
        return score;
    }

    private static void ExpandConnectedEvidence(
        EvidenceGraph graph,
        List<EvidenceNode> selected,
        HashSet<string> selectedIds)
    {
        var nodesById = graph.Nodes.ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
        for (var pass = 0; pass < 2 && selected.Count < MaxExpandedNodes; pass++)
        {
            var frontier = selectedIds.ToArray();
            foreach (var edge in graph.Edges.Where(edge =>
                         frontier.Contains(edge.SourceId, StringComparer.OrdinalIgnoreCase) ||
                         frontier.Contains(edge.TargetId, StringComparer.OrdinalIgnoreCase)))
            {
                var candidateId = selectedIds.Contains(edge.SourceId) ? edge.TargetId : edge.SourceId;
                if (!selectedIds.Add(candidateId) || !nodesById.TryGetValue(candidateId, out var candidate))
                {
                    continue;
                }

                if (IsTechnicalInventoryKind(candidate.Kind))
                {
                    selectedIds.Remove(candidateId);
                    continue;
                }

                selected.Add(candidate);
                if (selected.Count >= MaxExpandedNodes)
                {
                    return;
                }
            }
        }
    }

    private static IEnumerable<string> BuildRelationshipLines(
        IEnumerable<EvidenceEdge> edges,
        IReadOnlyDictionary<string, EvidenceNode> nodes,
        IDictionary<string, EvidenceCitation> citations)
    {
        foreach (var edge in edges)
        {
            if (!nodes.TryGetValue(edge.SourceId, out var source) || !nodes.TryGetValue(edge.TargetId, out var target))
            {
                continue;
            }

            var statement = edge.Relation switch
            {
                "implemented-by" => $"配置项 {source.Label} 对应 JavaScript 函数 {target.Label}",
                "executes" => $"Ribbon 命令 {source.Label} 执行 {target.Label}",
                "binds-command" => $"按钮 {source.Label} 绑定命令 {target.Label}",
                "may-trigger-plugin-step" => $"{source.Label} 在运行到该分支时可能触发插件步骤 {target.Label}",
                "handled-by" => $"插件步骤 {source.Label} 由类型 {target.Label} 处理",
                "implemented-in" => $"插件类型 {source.Label} 位于程序集 {target.Label}",
                "matches-managed-type" => $"注册类型 {source.Label} 与 DLL 元数据类型 {target.Label} 一致",
                "matches-decompiled-type" => $"注册类型 {source.Label} 与反编译源码类型 {target.Label} 一致",
                "declares-execute" => $"反编译插件类型 {source.Label} 声明 Execute 入口 {target.Label}",
                "declares-method" when target.Label.EndsWith(".Execute", StringComparison.OrdinalIgnoreCase) =>
                    $"插件类型 {source.Label} 声明入口方法 Execute",
                "visible-when" => $"命令 {source.Label} 的显示受规则 {target.Label} 控制",
                "enabled-when" => $"命令 {source.Label} 的启用受规则 {target.Label} 控制",
                _ => null
            };
            if (statement is null)
            {
                continue;
            }

            AddCitation(citations, source);
            AddCitation(citations, target);
            var prefix = edge.Confidence == EvidenceConfidence.Inferred ? "推断：" : string.Empty;
            yield return $"{prefix}{statement}。{Cite(source)} {Cite(target)}";
        }
    }

    private static IReadOnlyList<string> BuildUnknowns(
        string question,
        EvidenceGraph graph,
        IReadOnlyCollection<EvidenceNode> selected)
    {
        var result = new List<string>();
        var asksPlugin = ContainsAny(question.ToLowerInvariant(), "插件", "plugin", "dll", "程序集", "反编译");
        if (selected.Any(node => node.Kind is "JavaScriptBehavior" or "PluginStep" or "CSharpPluginBehavior") || IsBroadLogicQuestion(question))
        {
            result.Add("静态分析能证明配置和可能调用链，但不能证明某次操作实际进入了哪个条件分支");
        }

        if (asksPlugin && graph.Nodes.All(node => node.Kind is not ("CSharpPluginBehavior" or "CSharpExecuteMethod" or "DecompiledPluginExcerpt")))
        {
            result.Add("当前快照尚未包含相关插件步骤的反编译证据，无法说明插件内部实现");
        }

        if (graph.Nodes.Any(node => node.Kind == "PluginAssembly" &&
                                         string.Equals(GetProperty(node, "SourceTypeName"), "Disk", StringComparison.OrdinalIgnoreCase)))
        {
            result.Add("磁盘部署 DLL 不在 CRM 数据库内容中，需要服务器文件权限或管理员另行提供文件");
        }

        foreach (var warning in graph.Warnings.Take(4))
        {
            if (!result.Contains(warning, StringComparer.Ordinal))
            {
                result.Add(warning.Trim().TrimEnd('.'));
            }
        }

        return result;
    }

    private static IEnumerable<string> Tokenize(string question)
    {
        var buffer = new StringBuilder();
        foreach (var character in question)
        {
            if (char.IsLetterOrDigit(character) || character is '_' or '.')
            {
                buffer.Append(character);
            }
            else if (buffer.Length > 0)
            {
                yield return buffer.ToString();
                buffer.Clear();
            }
        }

        if (buffer.Length > 0)
        {
            yield return buffer.ToString();
        }
    }

    private static string BuildSearchableText(EvidenceNode node)
    {
        var builder = new StringBuilder();
        builder.Append(node.Kind).Append(' ').Append(node.Label).Append(' ').Append(node.Summary)
            .Append(' ').Append(node.ArtifactName).Append(' ').Append(node.Location);
        if (node.Properties is not null)
        {
            foreach (var pair in node.Properties)
            {
                builder.Append(' ').Append(pair.Key).Append(' ').Append(pair.Value);
            }
        }

        return builder.ToString().ToLowerInvariant();
    }

    private static bool IsBroadLogicQuestion(string question) =>
        ContainsAny(question.ToLowerInvariant(), "逻辑", "做什么", "会发生什么", "业务", "当前窗体", "说明", "解释");

    private static bool IsBusinessLogicKind(string kind) => kind is
        "FormEvent" or "FormHandler" or "RibbonButton" or "RibbonCommand" or
        "RibbonJavaScriptAction" or "RibbonRule" or "JavaScriptFunction" or
        "JavaScriptBehavior" or "PluginStep" or "PluginType" or "PluginAssembly" or
        "CSharpPluginType" or "CSharpExecuteMethod" or "CSharpPluginBehavior" or "DecompiledPluginExcerpt";

    private static bool IsTechnicalInventoryKind(string kind) => kind is "ManagedType" or "ManagedMethod";

    private static int KindPriority(string kind) => kind switch
    {
        "PageContext" => 0,
        "FormEvent" => 10,
        "FormHandler" => 11,
        "RibbonButton" => 20,
        "RibbonCommand" => 21,
        "RibbonJavaScriptAction" => 22,
        "RibbonRule" => 23,
        "JavaScriptFunction" => 30,
        "JavaScriptBehavior" => 31,
        "PluginStep" => 40,
        "PluginType" => 41,
        "PluginAssembly" => 42,
        "CSharpPluginType" => 45,
        "CSharpExecuteMethod" => 46,
        "CSharpPluginBehavior" => 47,
        "DecompiledPluginExcerpt" => 48,
        _ => 100
    };

    private static int EdgePriority(string relation) => relation switch
    {
        "binds-command" => 0,
        "executes" => 1,
        "implemented-by" => 2,
        "may-trigger-plugin-step" => 3,
        "handled-by" => 4,
        "implemented-in" => 5,
        "matches-managed-type" => 6,
        "matches-decompiled-type" => 7,
        "declares-execute" => 8,
        "declares-method" => 9,
        _ => 100
    };

    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static string EnsureTerminalPunctuation(string value) =>
        value.EndsWith('。') || value.EndsWith('.') || value.EndsWith('！') || value.EndsWith('？')
            ? value
            : value + "。";

    private static string Cite(EvidenceNode node) => $"【证据:{node.Id}】";

    private static void AddCitation(IDictionary<string, EvidenceCitation> citations, EvidenceNode node)
    {
        citations.TryAdd(node.Id, new EvidenceCitation(
            node.Id,
            node.Label,
            node.ArtifactName,
            node.Location,
            node.Confidence));
    }

    private static string? GetProperty(EvidenceNode node, string name) =>
        node.Properties?.TryGetValue(name, out var value) == true ? value : null;

    private static bool SameNormalizedComponent(string first, string second) =>
        string.Equals(EvidenceId.NormalizeExternal(first), EvidenceId.NormalizeExternal(second),
            StringComparison.OrdinalIgnoreCase);
}
