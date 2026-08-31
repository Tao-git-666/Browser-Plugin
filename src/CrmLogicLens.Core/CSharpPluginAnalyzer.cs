using System.Text;
using System.Text.RegularExpressions;

namespace CrmLogicLens.Core;

/// <summary>
/// Bounded lexical analysis for C# emitted by a trusted decompiler worker. It never
/// compiles or executes the source and deliberately avoids Roslyn/workspace loading.
/// </summary>
public sealed class CSharpPluginAnalyzer
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);
    private const RegexOptions SafeOptions = RegexOptions.CultureInvariant |
                                              RegexOptions.IgnoreCase |
                                              RegexOptions.NonBacktracking;

    private static readonly Regex PluginClassRegex = new(
        @"\bclass\s+(?<name>[A-Za-z_][A-Za-z0-9_`]*)\s*(?::\s*(?<bases>[^\{\r\n]{0,1000}))?\s*\{",
        SafeOptions, RegexTimeout);
    private static readonly Regex NamespaceRegex = new(
        @"\bnamespace\s+(?<name>[A-Za-z_][A-Za-z0-9_.]*)",
        SafeOptions, RegexTimeout);
    private static readonly Regex ExecuteRegex = new(
        @"\b(?:public\s+|private\s+|protected\s+|internal\s+|static\s+|virtual\s+|sealed\s+|override\s+|async\s+)*void\s+Execute\s*\(",
        SafeOptions, RegexTimeout);
    private static readonly Regex ServiceVariableRegex = new(
        @"\bIOrganizationService\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)",
        SafeOptions, RegexTimeout);
    private static readonly Regex EntityVariableRegex = new(
        "\\b(?:var|Entity)\\s+(?<variable>[A-Za-z_][A-Za-z0-9_]*)\\s*=\\s*new\\s+Entity\\s*\\(\\s*\"(?<entity>[A-Za-z0-9_]+)\"",
        SafeOptions, RegexTimeout);
    private static readonly Regex EntityFieldRegex = new(
        "(?<variable>[A-Za-z_][A-Za-z0-9_]*)\\s*(?:\\.Attributes\\s*)?\\[\\s*\"(?<field>[A-Za-z0-9_]+)\"\\s*\\]\\s*=",
        SafeOptions, RegexTimeout);
    private static readonly Regex ServiceCallRegex = new(
        @"\b(?<service>[A-Za-z_][A-Za-z0-9_]*)\s*\.\s*(?<operation>Create|Update|Delete|Retrieve|RetrieveMultiple|Execute)\s*\(\s*(?<first>[^,\)\r\n]{0,300})",
        SafeOptions, RegexTimeout);
    private static readonly Regex MessageNameRegex = new(
        "\\.MessageName\\s*(?<operator>==|!=)\\s*\"(?<value>[A-Za-z0-9_.]+)\"",
        SafeOptions, RegexTimeout);
    private static readonly Regex PrimaryEntityRegex = new(
        "\\.PrimaryEntityName\\s*(?<operator>==|!=)\\s*\"(?<value>[A-Za-z0-9_]+)\"",
        SafeOptions, RegexTimeout);
    private static readonly Regex InputParameterRegex = new(
        "\\.InputParameters\\s*\\[\\s*\"(?<key>[A-Za-z0-9_]+)\"\\s*\\]",
        SafeOptions, RegexTimeout);
    private static readonly Regex InputContainsRegex = new(
        "\\.InputParameters\\s*\\.\\s*Contains\\s*\\(\\s*\"(?<key>[A-Za-z0-9_]+)\"",
        SafeOptions, RegexTimeout);
    private static readonly Regex PluginExceptionRegex = new(
        "\\bthrow\\s+new\\s+InvalidPluginExecutionException\\s*(?:\\(\\s*\"(?<message>[^\"\\r\\n]{0,500})\")?",
        SafeOptions, RegexTimeout);
    private static readonly Regex OrganizationRequestRegex = new(
        "\\bnew\\s+(?:(?<request>[A-Za-z_][A-Za-z0-9_]*Request)\\b|OrganizationRequest\\s*\\(\\s*\"(?<name>[A-Za-z0-9_.]+)\")",
        SafeOptions, RegexTimeout);
    private static readonly Regex ExternalCallRegex = new(
        @"\b(?<api>HttpClient|HttpRequestMessage|HttpWebRequest|WebRequest|WebClient|SqlConnection|SqlCommand|ServiceClient|RestClient)\b|\.\s*(?<method>SendAsync|GetAsync|PostAsync|PutAsync|DeleteAsync)\s*\(",
        SafeOptions, RegexTimeout);

    public EvidenceGraph Analyze(DecodedArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var builder = new EvidenceGraphBuilder();
        if (artifact.Kind != ArtifactKind.DecompiledCSharp || artifact.Text is null)
        {
            builder.AddWarning($"{artifact.Name}: artifact is not decompiled C# text.");
            return builder.Build();
        }

        try
        {
            var source = MaskComments(artifact.Text);
            var lineMap = new LineMap(source);
            var sourceId = EvidenceId.Create("decompiled-csharp", artifact.ComponentId, artifact.Name);
            builder.AddNode(new EvidenceNode(
                sourceId,
                "DecompiledCSharpSource",
                artifact.Name,
                "隔离反编译器输出的 C# 源码；本阶段只做文本静态分析，不编译、不执行。",
                artifact.Name,
                "decompiled source",
                EvidenceConfidence.Confirmed,
                EvidenceProperties.Create(
                    ("AssemblyId", artifact.ComponentId),
                    ("Version", artifact.Version),
                    ("AnalysisMode", "LexicalStaticOnly"))));

            var pluginCount = 0;
            foreach (Match classMatch in PluginClassRegex.Matches(source))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!classMatch.Groups["bases"].Value.Contains("IPlugin", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var openBrace = source.IndexOf('{', classMatch.Index + classMatch.Length - 1);
                var closeBrace = FindMatchingBrace(source, openBrace);
                if (openBrace < 0 || closeBrace < 0)
                {
                    builder.AddWarning($"{artifact.Name}: could not determine class boundary at line {lineMap.Line(classMatch.Index)}.");
                    continue;
                }

                pluginCount++;
                var simpleName = classMatch.Groups["name"].Value;
                var typeName = QualifyTypeName(source, classMatch.Index, simpleName);
                var typeId = EvidenceId.Create("csharp-plugin-type", artifact.ComponentId, typeName);
                builder.AddNode(new EvidenceNode(
                    typeId,
                    "CSharpPluginType",
                    typeName,
                    $"反编译源码中的类型 {typeName} 实现 IPlugin。",
                    artifact.Name,
                    $"line {lineMap.Line(classMatch.Index)}: class {typeName}",
                    EvidenceConfidence.Confirmed,
                    EvidenceProperties.Create(
                        ("AssemblyId", artifact.ComponentId),
                        ("TypeName", typeName),
                        ("ImplementsIPlugin", true))));
                builder.AddEdge(new EvidenceEdge(sourceId, typeId, "defines-plugin-type", EvidenceConfidence.Confirmed));
                AnalyzePluginClass(
                    source,
                    openBrace + 1,
                    closeBrace,
                    artifact,
                    typeName,
                    typeId,
                    lineMap,
                    builder,
                    cancellationToken);
            }

            if (pluginCount == 0)
            {
                builder.AddWarning($"{artifact.Name}: no class implementing IPlugin was found in decompiled C#.");
            }
        }
        catch (RegexMatchTimeoutException)
        {
            builder.AddWarning($"{artifact.Name}: C# lexical analysis exceeded its regex time limit.");
        }
        catch (InvalidOperationException exception)
        {
            builder.AddWarning($"{artifact.Name}: C# lexical analysis stopped ({exception.Message}).");
        }

        return builder.Build();
    }

    private static void AnalyzePluginClass(
        string source,
        int classStart,
        int classEnd,
        DecodedArtifact artifact,
        string typeName,
        string typeId,
        LineMap lineMap,
        EvidenceGraphBuilder builder,
        CancellationToken cancellationToken)
    {
        var classText = source[classStart..classEnd];
        var executeMatches = ExecuteRegex.Matches(classText);
        if (executeMatches.Count == 0)
        {
            builder.AddWarning($"{artifact.Name}: IPlugin type {typeName} has no recognizable Execute method.");
            return;
        }

        foreach (Match executeMatch in executeMatches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var absoluteMethodIndex = classStart + executeMatch.Index;
            var methodOpenBrace = source.IndexOf('{', absoluteMethodIndex + executeMatch.Length);
            if (methodOpenBrace < 0 || methodOpenBrace >= classEnd)
            {
                continue;
            }

            var methodCloseBrace = FindMatchingBrace(source, methodOpenBrace);
            if (methodCloseBrace < 0 || methodCloseBrace > classEnd)
            {
                methodCloseBrace = classEnd;
            }

            var executeId = EvidenceId.Create("csharp-execute", artifact.ComponentId, typeName, absoluteMethodIndex.ToString());
            builder.AddNode(new EvidenceNode(
                executeId,
                "CSharpExecuteMethod",
                $"{typeName}.Execute",
                $"插件类型 {typeName} 的 Execute 入口。",
                artifact.Name,
                $"line {lineMap.Line(absoluteMethodIndex)}: Execute",
                EvidenceConfidence.Confirmed,
                EvidenceProperties.Create(
                    ("AssemblyId", artifact.ComponentId),
                    ("TypeName", typeName),
                    ("MethodName", "Execute"))));
            builder.AddEdge(new EvidenceEdge(typeId, executeId, "declares-execute", EvidenceConfidence.Confirmed));
            AnalyzeExecuteBody(
                source,
                methodOpenBrace + 1,
                methodCloseBrace,
                artifact,
                typeName,
                executeId,
                lineMap,
                builder,
                cancellationToken);
        }
    }

    private static void AnalyzeExecuteBody(
        string source,
        int bodyStart,
        int bodyEnd,
        DecodedArtifact artifact,
        string typeName,
        string executeId,
        LineMap lineMap,
        EvidenceGraphBuilder builder,
        CancellationToken cancellationToken)
    {
        var body = source[bodyStart..bodyEnd];
        var serviceVariables = ServiceVariableRegex.Matches(body)
            .Select(match => match.Groups["name"].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        serviceVariables.Add("service");
        serviceVariables.Add("organizationService");
        serviceVariables.Add("orgService");

        var entityByVariable = EntityVariableRegex.Matches(body)
            .GroupBy(match => match.Groups["variable"].Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Groups["entity"].Value, StringComparer.OrdinalIgnoreCase);
        var fieldsByVariable = EntityFieldRegex.Matches(body)
            .GroupBy(match => match.Groups["variable"].Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(match => match.Groups["field"].Value)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var requestNames = OrganizationRequestRegex.Matches(body)
            .Select(match => match.Groups["name"].Success
                ? match.Groups["name"].Value
                : match.Groups["request"].Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (Match match in ServiceCallRegex.Matches(body))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var service = match.Groups["service"].Value;
            if (!serviceVariables.Contains(service))
            {
                continue;
            }

            var operation = match.Groups["operation"].Value;
            var firstArgument = match.Groups["first"].Value.Trim();
            var entity = ExtractQuoted(firstArgument);
            if (entity is null)
            {
                entityByVariable.TryGetValue(FirstIdentifier(firstArgument), out entity);
            }

            IReadOnlyList<string> fields = [];
            if (fieldsByVariable.TryGetValue(FirstIdentifier(firstArgument), out var assignedFields))
            {
                fields = assignedFields;
            }

            string? customOperation = null;
            if (operation.Equals("Execute", StringComparison.OrdinalIgnoreCase) && requestNames.Length == 1)
            {
                customOperation = requestNames[0];
            }

            var summary = operation.Equals("Execute", StringComparison.OrdinalIgnoreCase)
                ? $"调用 IOrganizationService.Execute 执行 {customOperation ?? "动态 OrganizationRequest"}。"
                : $"调用 IOrganizationService.{operation}{(entity is null ? string.Empty : $" 操作实体 {entity}")}" +
                  (fields.Count == 0 ? "。" : $"；涉及字段 {string.Join(", ", fields)}。");
            AddBehavior(
                artifact,
                typeName,
                executeId,
                builder,
                lineMap,
                bodyStart + match.Index,
                "OrganizationServiceCall",
                summary,
                EvidenceConfidence.Confirmed,
                ("Operation", customOperation ?? operation),
                ("ServiceOperation", operation),
                ("Entity", entity),
                ("Fields", string.Join(",", fields)));
        }

        AddContextMatches(MessageNameRegex, body, bodyStart, artifact, typeName, executeId, lineMap, builder,
            "MessageNameCondition", "MessageName", "插件代码检查消息名称", cancellationToken);
        AddContextMatches(PrimaryEntityRegex, body, bodyStart, artifact, typeName, executeId, lineMap, builder,
            "PrimaryEntityCondition", "PrimaryEntityName", "插件代码检查主实体", cancellationToken);

        foreach (Match match in InputParameterRegex.Matches(body).Cast<Match>()
                     .Concat(InputContainsRegex.Matches(body).Cast<Match>())
                     .OrderBy(match => match.Index))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = match.Groups["key"].Value;
            AddBehavior(
                artifact, typeName, executeId, builder, lineMap, bodyStart + match.Index,
                "InputParameterAccess",
                $"读取或检查插件上下文 InputParameters[\"{key}\"]。",
                EvidenceConfidence.Confirmed,
                ("Parameter", key), ("Access", "ReadOrCheck"));
        }

        foreach (Match match in PluginExceptionRegex.Matches(body))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var message = match.Groups["message"].Success ? match.Groups["message"].Value : null;
            AddBehavior(
                artifact, typeName, executeId, builder, lineMap, bodyStart + match.Index,
                "PluginException",
                message is null
                    ? "代码抛出 InvalidPluginExecutionException，可能阻止同步操作。"
                    : $"代码抛出 InvalidPluginExecutionException（“{Truncate(message, 180)}”），可能阻止同步操作。",
                EvidenceConfidence.Confirmed,
                ("ExceptionType", "InvalidPluginExecutionException"), ("Message", message));
        }

        foreach (Match match in ExternalCallRegex.Matches(body))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var api = match.Groups["api"].Success ? match.Groups["api"].Value : match.Groups["method"].Value;
            AddBehavior(
                artifact, typeName, executeId, builder, lineMap, bodyStart + match.Index,
                "ExternalCall",
                $"代码使用 {api}，表明插件可能访问外部 HTTP、数据库或服务端资源。",
                EvidenceConfidence.Inferred,
                ("ExternalApi", api));
        }
    }

    private static void AddContextMatches(
        Regex regex,
        string body,
        int bodyStart,
        DecodedArtifact artifact,
        string typeName,
        string executeId,
        LineMap lineMap,
        EvidenceGraphBuilder builder,
        string behavior,
        string property,
        string summaryPrefix,
        CancellationToken cancellationToken)
    {
        foreach (Match match in regex.Matches(body))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = match.Groups["value"].Value;
            var comparison = match.Groups["operator"].Value;
            AddBehavior(
                artifact, typeName, executeId, builder, lineMap, bodyStart + match.Index,
                behavior,
                $"{summaryPrefix}：{property} {comparison} {value}。",
                EvidenceConfidence.Confirmed,
                (property, value), ("Comparison", comparison));
        }
    }

    private static void AddBehavior(
        DecodedArtifact artifact,
        string typeName,
        string executeId,
        EvidenceGraphBuilder builder,
        LineMap lineMap,
        int index,
        string behavior,
        string summary,
        EvidenceConfidence confidence,
        params (string Key, object? Value)[] properties)
    {
        var id = EvidenceId.Create("csharp-behavior", artifact.ComponentId, typeName, behavior, index.ToString());
        var allProperties = new List<(string Key, object? Value)>
        {
            ("Behavior", behavior),
            ("AssemblyId", artifact.ComponentId),
            ("TypeName", typeName),
            ("MethodName", "Execute")
        };
        allProperties.AddRange(properties);
        builder.AddNode(new EvidenceNode(
            id,
            "CSharpPluginBehavior",
            behavior,
            summary,
            artifact.Name,
            $"line {lineMap.Line(index)}: {behavior}",
            confidence,
            EvidenceProperties.Create(allProperties.ToArray())));
        builder.AddEdge(new EvidenceEdge(executeId, id, "performs", confidence));
    }

    private static string QualifyTypeName(string source, int classIndex, string simpleName)
    {
        Match? selected = null;
        foreach (Match match in NamespaceRegex.Matches(source))
        {
            if (match.Index >= classIndex)
            {
                break;
            }

            selected = match;
        }

        return selected is null ? simpleName : $"{selected.Groups["name"].Value}.{simpleName}";
    }

    private static int FindMatchingBrace(string source, int openBrace)
    {
        if (openBrace < 0 || openBrace >= source.Length || source[openBrace] != '{')
        {
            return -1;
        }

        var depth = 0;
        var inString = false;
        var inChar = false;
        var verbatim = false;
        var escaped = false;
        for (var index = openBrace; index < source.Length; index++)
        {
            var character = source[index];
            if (inString)
            {
                if (verbatim && character == '"' && index + 1 < source.Length && source[index + 1] == '"')
                {
                    index++;
                    continue;
                }

                if (!verbatim && escaped)
                {
                    escaped = false;
                    continue;
                }

                if (!verbatim && character == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (inChar)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '\'')
                {
                    inChar = false;
                }

                continue;
            }

            if (character == '"')
            {
                inString = true;
                verbatim = index > 0 && source[index - 1] == '@';
                continue;
            }

            if (character == '\'')
            {
                inChar = true;
                continue;
            }

            if (character == '{')
            {
                depth++;
            }
            else if (character == '}' && --depth == 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static string MaskComments(string source)
    {
        var result = source.ToCharArray();
        var inString = false;
        var inChar = false;
        var verbatim = false;
        var escaped = false;
        for (var index = 0; index < result.Length; index++)
        {
            var character = result[index];
            if (inString)
            {
                if (verbatim && character == '"' && index + 1 < result.Length && result[index + 1] == '"')
                {
                    index++;
                }
                else if (!verbatim && escaped)
                {
                    escaped = false;
                }
                else if (!verbatim && character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (inChar)
            {
                if (escaped) escaped = false;
                else if (character == '\\') escaped = true;
                else if (character == '\'') inChar = false;
                continue;
            }

            if (character == '"')
            {
                inString = true;
                verbatim = index > 0 && result[index - 1] == '@';
                continue;
            }

            if (character == '\'')
            {
                inChar = true;
                continue;
            }

            if (character == '/' && index + 1 < result.Length && result[index + 1] == '/')
            {
                result[index] = result[index + 1] = ' ';
                index += 2;
                while (index < result.Length && result[index] is not '\r' and not '\n') result[index++] = ' ';
                index--;
            }
            else if (character == '/' && index + 1 < result.Length && result[index + 1] == '*')
            {
                result[index] = result[index + 1] = ' ';
                index += 2;
                while (index + 1 < result.Length && !(result[index] == '*' && result[index + 1] == '/'))
                {
                    if (result[index] is not '\r' and not '\n') result[index] = ' ';
                    index++;
                }

                if (index + 1 < result.Length) result[index] = result[index + 1] = ' ';
                index++;
            }
        }

        return new string(result);
    }

    private static string FirstIdentifier(string expression)
    {
        var builder = new StringBuilder();
        foreach (var character in expression.TrimStart())
        {
            if (char.IsLetterOrDigit(character) || character == '_') builder.Append(character);
            else break;
        }

        return builder.ToString();
    }

    private static string? ExtractQuoted(string value)
    {
        var first = value.IndexOf('"');
        if (first < 0) return null;
        var second = value.IndexOf('"', first + 1);
        return second > first + 1 ? value[(first + 1)..second] : null;
    }

    private static string Truncate(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum] + "…";

    private sealed class LineMap
    {
        private readonly int[] _lineStarts;

        public LineMap(string source)
        {
            var starts = new List<int> { 0 };
            for (var index = 0; index < source.Length; index++)
            {
                if (source[index] == '\n') starts.Add(index + 1);
            }

            _lineStarts = starts.ToArray();
        }

        public int Line(int index)
        {
            var position = Array.BinarySearch(_lineStarts, Math.Max(0, index));
            return position >= 0 ? position + 1 : ~position;
        }
    }
}
