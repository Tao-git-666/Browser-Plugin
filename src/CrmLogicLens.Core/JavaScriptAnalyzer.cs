using Esprima;
using Esprima.Ast;

namespace CrmLogicLens.Core;

/// <summary>
/// Static ECMAScript analysis backed by Esprima. The source is parsed but never executed.
/// </summary>
public sealed class JavaScriptAnalyzer
{
    private const int MaxAstNodes = 300_000;
    private const int MaxLexicalNesting = 384;

    public EvidenceGraph Analyze(DecodedArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var builder = new EvidenceGraphBuilder();
        if (artifact.Kind != ArtifactKind.JavaScript || artifact.Text is null)
        {
            builder.AddWarning($"{artifact.Name}: artifact is not JavaScript text.");
            return builder.Build();
        }

        if (!HasSafeNesting(artifact.Text))
        {
            builder.AddWarning($"{artifact.Name}: JavaScript nesting exceeds the static-analysis safety limit.");
            return builder.Build();
        }

        try
        {
            var nodeCount = 0;
            var options = new ParserOptions
            {
                Tolerant = true,
                MaxAssignmentDepth = 192,
                OnNodeCreated = _ =>
                {
                    nodeCount++;
                    if (nodeCount > MaxAstNodes)
                    {
                        throw new InvalidOperationException("AST node limit exceeded.");
                    }

                    if ((nodeCount & 1023) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                }
            };
            var parser = new JavaScriptParser(options);
            var program = parser.ParseScript(artifact.Text);
            var fileId = EvidenceId.Create("javascript", artifact.ComponentId, artifact.Name);
            builder.AddNode(new EvidenceNode(
                fileId,
                "JavaScriptFile",
                artifact.Name,
                "已通过 ECMAScript AST 静态解析的 JavaScript Web Resource。",
                artifact.Name,
                "script",
                EvidenceConfidence.Confirmed,
                EvidenceProperties.Create(
                    ("ComponentId", artifact.ComponentId),
                    ("AstNodeCount", nodeCount),
                    ("Parser", "Esprima"))));

            var operationNames = FindOperationNames(program).ToArray();
            var state = new WalkState(artifact, artifact.Text, builder, fileId, operationNames, cancellationToken);
            Walk(program, parent: null, currentFunction: null, currentNamespace: null, state, depth: 0);
        }
        catch (ParserException exception)
        {
            builder.AddWarning($"{artifact.Name}: JavaScript syntax could not be parsed ({exception.Description ?? exception.Message}).");
        }
        catch (InvalidOperationException exception)
        {
            builder.AddWarning($"{artifact.Name}: JavaScript analysis stopped ({exception.Message}).");
        }
        catch (ArgumentException exception)
        {
            builder.AddWarning($"{artifact.Name}: JavaScript analysis rejected invalid syntax ({exception.Message}).");
        }

        return builder.Build();
    }

    private static void Walk(
        Node node,
        Node? parent,
        FunctionContext? currentFunction,
        string? currentNamespace,
        WalkState state,
        int depth)
    {
        state.CancellationToken.ThrowIfCancellationRequested();
        if (depth > MaxLexicalNesting)
        {
            throw new InvalidOperationException("AST nesting limit exceeded.");
        }

        var namespaceForChildren = currentNamespace;
        if (currentFunction is null && node is VariableDeclarator declarator && declarator.Id is Identifier namespaceIdentifier)
        {
            namespaceForChildren = namespaceIdentifier.Name;
        }

        var functionForChildren = currentFunction;
        if (TryCreateFunctionContext(node, parent, namespaceForChildren, out var function))
        {
            functionForChildren = function;
            AddFunction(function, node, state);
        }

        if (node is CallExpression call)
        {
            AnalyzeCall(call, functionForChildren, state);
        }

        foreach (var child in node.ChildNodes)
        {
            if (child is not null)
            {
                Walk(child, node, functionForChildren, namespaceForChildren, state, depth + 1);
            }
        }
    }

    private static bool TryCreateFunctionContext(
        Node node,
        Node? parent,
        string? currentNamespace,
        out FunctionContext function)
    {
        string? name = node switch
        {
            FunctionDeclaration declaration => declaration.Id?.Name,
            FunctionExpression expression => expression.Id?.Name ?? DeriveAssignedName(parent),
            ArrowFunctionExpression => DeriveAssignedName(parent),
            _ => null
        };

        if (node is not IFunction || string.IsNullOrWhiteSpace(name))
        {
            function = default!;
            return false;
        }

        var qualifiedName = name.Contains('.', StringComparison.Ordinal) || string.IsNullOrWhiteSpace(currentNamespace)
            ? name
            : $"{currentNamespace}.{name}";
        function = new FunctionContext(name, qualifiedName);
        return true;
    }

    private static string? DeriveAssignedName(Node? parent) => parent switch
    {
        VariableDeclarator { Id: Identifier identifier } => identifier.Name,
        AssignmentExpression assignment => GetExpressionPath(assignment.Left),
        Property property => GetPropertyName(property.Key),
        _ => null
    };

    private static void AddFunction(FunctionContext function, Node node, WalkState state)
    {
        var functionId = FunctionId(state.Artifact.Name, function.QualifiedName);
        state.Builder.AddNode(new EvidenceNode(
            functionId,
            "JavaScriptFunction",
            function.QualifiedName,
            $"JavaScript 函数 {function.QualifiedName}。",
            state.Artifact.Name,
            NodeLocation(node, $"function {function.QualifiedName}"),
            EvidenceConfidence.Confirmed,
            EvidenceProperties.Create(
                ("FunctionName", function.Name),
                ("QualifiedFunctionName", function.QualifiedName),
                ("Library", state.Artifact.Name))));
        state.Builder.AddEdge(new EvidenceEdge(state.FileId, functionId, "defines-function", EvidenceConfidence.Confirmed));
    }

    private static void AnalyzeCall(CallExpression call, FunctionContext? function, WalkState state)
    {
        if (call.Callee is not MemberExpression member)
        {
            AnalyzeFetchCall(call, function, state);
            return;
        }

        var method = GetPropertyName(member.Property);
        if (string.IsNullOrWhiteSpace(method))
        {
            return;
        }

        if (TryGetBoundField(member.Object, "getAttribute", out var attribute))
        {
            switch (method)
            {
                case "getValue":
                    AddBehavior(call, function, "FieldRead", attribute,
                        $"读取字段 {attribute} 的值。", EvidenceConfidence.Confirmed, state,
                        ("Access", "Read"));
                    return;
                case "setValue":
                    AddBehavior(call, function, "FieldWrite", attribute,
                        $"写入字段 {attribute} 的值{ArgumentSuffix(call, 0, state.Source)}。",
                        EvidenceConfidence.Confirmed, state,
                        ("Access", "Write"), ("Value", GetArgumentSource(call, 0, state.Source)));
                    return;
                case "setRequiredLevel":
                    var level = GetLiteralString(ArgumentAt(call, 0)) ?? GetArgumentSource(call, 0, state.Source);
                    AddBehavior(call, function, "FieldRequiredLevel", attribute,
                        $"把字段 {attribute} 的必填级别设置为 {level ?? "动态值"}。",
                        EvidenceConfidence.Confirmed, state, ("RequiredLevel", level));
                    return;
                case "addOnChange":
                case "removeOnChange":
                    var handler = GetExpressionPath(ArgumentAt(call, 0));
                    AddBehavior(call, function, "DynamicFieldEvent", attribute,
                        $"为字段 {attribute} {TranslateMethod(method)}处理器 {handler ?? "动态函数"}。",
                        EvidenceConfidence.Confirmed, state,
                        ("Event", "onchange"), ("Handler", handler), ("Operation", method));
                    return;
            }
        }

        if (TryGetBoundField(member.Object, "getControl", out var control))
        {
            if (method is "setVisible" or "setDisabled")
            {
                var value = GetLiteralString(ArgumentAt(call, 0)) ?? GetArgumentSource(call, 0, state.Source);
                var behavior = method == "setVisible" ? "ControlVisibility" : "ControlDisabledState";
                var verb = method == "setVisible" ? "可见状态" : "禁用状态";
                AddBehavior(call, function, behavior, control,
                    $"把控件 {control} 的{verb}设置为 {value ?? "动态值"}。",
                    EvidenceConfidence.Confirmed, state,
                    ("Control", control), ("Value", value));
                return;
            }
        }

        var path = GetExpressionPath(call.Callee);
        if (method is "invokeHiddenApiAsync" or "invokeCustomApiAsync" or "invokeActionAsync")
        {
            var operation = GetLiteralString(ArgumentAt(call, 0));
            var route = GetLiteralString(ArgumentAt(call, 1));
            var fields = GetObjectFields(ArgumentAt(call, 2));
            AddBehavior(call, function, "CustomApiAction", operation,
                $"通过 {path ?? method} 调用 Dataverse 自定义 API/Action {operation ?? "（名称需在运行时确定）"}" +
                (string.IsNullOrWhiteSpace(route) ? "。" : $"，业务路由 {route}。"),
                operation is null ? EvidenceConfidence.Inferred : EvidenceConfidence.Confirmed,
                state,
                ("Operation", operation),
                ("Route", route),
                ("Fields", string.Join(",", fields)),
                ("ApiMethod", path ?? method));
            return;
        }
        if (method == "save" && path is not null &&
            (path.EndsWith(".data.save", StringComparison.OrdinalIgnoreCase) ||
             path.EndsWith(".data.entity.save", StringComparison.OrdinalIgnoreCase)))
        {
            AddBehavior(call, function, "FormSave", null,
                "发起当前窗体记录保存。", EvidenceConfidence.Confirmed, state,
                ("Operation", "Save"));
            return;
        }

        if (path is not null && path.Contains("Xrm.WebApi", StringComparison.OrdinalIgnoreCase))
        {
            AnalyzeWebApiCall(call, function, path, method, state);
            return;
        }

        AnalyzeOpenCall(call, function, method, state);
    }

    private static void AnalyzeWebApiCall(
        CallExpression call,
        FunctionContext? function,
        string path,
        string method,
        WalkState state)
    {
        var entity = GetLiteralString(ArgumentAt(call, 0));
        switch (method)
        {
            case "createRecord":
                AddWebApiBehavior(call, function, "WebApiCreate", "Create", entity,
                    GetObjectFields(ArgumentAt(call, 1)), state);
                break;
            case "updateRecord":
                AddWebApiBehavior(call, function, "WebApiUpdate", "Update", entity,
                    GetObjectFields(ArgumentAt(call, 2)), state);
                break;
            case "deleteRecord":
                AddWebApiBehavior(call, function, "WebApiDelete", "Delete", entity, [], state);
                break;
            case "retrieveRecord":
                AddWebApiBehavior(call, function, "WebApiRetrieve", "Retrieve", entity, [], state);
                break;
            case "retrieveMultipleRecords":
                var query = GetLiteralString(ArgumentAt(call, 1)) ?? GetArgumentSource(call, 1, state.Source);
                AddWebApiBehavior(call, function, "WebApiRetrieveMultiple", "RetrieveMultiple", entity, [], state,
                    ("Query", query));
                if (query?.Contains("fetchXml", StringComparison.OrdinalIgnoreCase) == true ||
                    query?.Contains("<fetch", StringComparison.OrdinalIgnoreCase) == true)
                {
                    AddBehavior(call, function, "FetchXmlQuery", entity,
                        $"对实体 {entity ?? "动态实体"} 执行 FetchXML 查询。",
                        EvidenceConfidence.Confirmed, state,
                        ("Operation", "RetrieveMultiple"), ("Entity", entity), ("Query", Truncate(query, 240)));
                }
                break;
            case "execute":
            case "executeMultiple":
                var directName = GetLiteralString(ArgumentAt(call, 0));
                var operation = directName ?? (state.OperationNames.Length == 1 ? state.OperationNames[0] : null);
                AddBehavior(call, function, "CustomApiAction", operation,
                    $"通过 Xrm.WebApi.{method} 调用自定义操作 {operation ?? "（名称需在运行时确定）"}。",
                    operation is null ? EvidenceConfidence.Inferred : EvidenceConfidence.Confirmed,
                    state,
                    ("Operation", operation), ("ApiMethod", path));
                break;
        }
    }

    private static void AddWebApiBehavior(
        CallExpression call,
        FunctionContext? function,
        string behavior,
        string operation,
        string? entity,
        IReadOnlyList<string> fields,
        WalkState state,
        params (string Key, object? Value)[] extras)
    {
        var fieldText = fields.Count == 0 ? string.Empty : $"；提交字段：{string.Join(", ", fields)}";
        var translated = operation switch
        {
            "Create" => "创建",
            "Update" => "更新",
            "Delete" => "删除",
            "Retrieve" => "读取",
            "RetrieveMultiple" => "查询多条",
            _ => operation
        };
        var properties = new List<(string Key, object? Value)>
        {
            ("Operation", operation),
            ("Entity", entity),
            ("Fields", string.Join(",", fields))
        };
        properties.AddRange(extras);
        AddBehavior(call, function, behavior, entity,
            $"通过 Xrm.WebApi {translated}实体 {entity ?? "（动态实体）"}{fieldText}。",
            entity is null ? EvidenceConfidence.Inferred : EvidenceConfidence.Confirmed,
            state,
            properties.ToArray());
    }

    private static void AnalyzeOpenCall(CallExpression call, FunctionContext? function, string method, WalkState state)
    {
        var path = GetExpressionPath(call.Callee);
        if (method.Equals("openWebResource", StringComparison.OrdinalIgnoreCase))
        {
            var target = GetLiteralString(ArgumentAt(call, 0));
            AddOpenPageBehavior(call, function, target, "webresource", path ?? method, state);
            return;
        }
        if (method.Equals("navigateTo", StringComparison.OrdinalIgnoreCase))
        {
            var pageInput = ArgumentAt(call, 0);
            var pageType = GetObjectString(pageInput, "pageType");
            var target = GetObjectString(pageInput, "webresourceName") ??
                         GetObjectString(pageInput, "name");
            AddOpenPageBehavior(
                call,
                function,
                target,
                pageType ?? "dynamic",
                path ?? method,
                state,
                GetArgumentSource(call, 0, state.Source));
            return;
        }

        string? url = null;
        string? httpMethod = null;
        if (method.Equals("open", StringComparison.OrdinalIgnoreCase))
        {
            httpMethod = GetLiteralString(ArgumentAt(call, 0));
            url = GetLiteralString(ArgumentAt(call, 1));
        }

        if (url is null || !LooksLikeDataverseOperationUrl(url))
        {
            return;
        }

        var operation = ExtractOperationFromUrl(url);
        AddBehavior(call, function, "CustomApiAction", operation,
            $"通过 HTTP {httpMethod ?? "请求"} 调用 Dataverse 操作 {operation ?? url}。",
            operation is null ? EvidenceConfidence.Inferred : EvidenceConfidence.Confirmed,
            state,
            ("Operation", operation), ("HttpMethod", httpMethod), ("Url", Truncate(url, 300)));
    }

    private static void AddOpenPageBehavior(
        CallExpression call,
        FunctionContext? function,
        string? target,
        string pageType,
        string apiMethod,
        WalkState state,
        string? source = null)
    {
        AddBehavior(
            call,
            function,
            "OpenCustomPage",
            target,
            $"通过 {apiMethod} 打开{TranslatePageType(pageType)} {target ?? "（目标名称在运行时确定）"}。",
            target is null ? EvidenceConfidence.Inferred : EvidenceConfidence.Confirmed,
            state,
            ("TargetName", target),
            ("PageType", pageType),
            ("ApiMethod", apiMethod),
            ("PageInput", source));
    }

    private static string TranslatePageType(string pageType) => pageType.ToLowerInvariant() switch
    {
        "webresource" => " HTML Web Resource",
        "custom" => "自定义页面",
        "entityrecord" => "记录页面",
        "entitylist" => "列表页面",
        _ => "页面"
    };

    private static void AnalyzeFetchCall(CallExpression call, FunctionContext? function, WalkState state)
    {
        var callee = GetExpressionPath(call.Callee);
        if (!string.Equals(callee, "fetch", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var url = GetLiteralString(ArgumentAt(call, 0));
        if (url is null || !LooksLikeDataverseOperationUrl(url))
        {
            return;
        }

        var operation = ExtractOperationFromUrl(url);
        AddBehavior(call, function, "CustomApiAction", operation,
            $"通过 fetch 调用 Dataverse 操作 {operation ?? url}。",
            operation is null ? EvidenceConfidence.Inferred : EvidenceConfidence.Confirmed,
            state,
            ("Operation", operation), ("Url", Truncate(url, 300)));
    }

    private static void AddBehavior(
        CallExpression call,
        FunctionContext? function,
        string behavior,
        string? subject,
        string summary,
        EvidenceConfidence confidence,
        WalkState state,
        params (string Key, object? Value)[] properties)
    {
        var functionName = function?.QualifiedName ?? "<global>";
        var location = NodeLocation(call, behavior);
        var behaviorId = EvidenceId.Create(
            "js-behavior",
            state.Artifact.Name,
            functionName,
            behavior,
            subject,
            call.Range.Start.ToString());
        var allProperties = new List<(string Key, object? Value)>
        {
            ("Behavior", behavior),
            ("Subject", subject),
            ("FunctionName", function?.Name ?? "<global>"),
            ("QualifiedFunctionName", functionName),
            ("Library", state.Artifact.Name)
        };
        allProperties.AddRange(properties);
        state.Builder.AddNode(new EvidenceNode(
            behaviorId,
            "JavaScriptBehavior",
            BehaviorLabel(behavior, subject),
            summary,
            state.Artifact.Name,
            location,
            confidence,
            EvidenceProperties.Create(allProperties.ToArray())));

        var functionId = FunctionId(state.Artifact.Name, functionName);
        if (function is null)
        {
            state.Builder.AddNode(new EvidenceNode(
                functionId,
                "JavaScriptFunction",
                "<global>",
                "JavaScript 文件顶层代码。",
                state.Artifact.Name,
                "script",
                EvidenceConfidence.Confirmed,
                EvidenceProperties.Create(
                    ("FunctionName", "<global>"),
                    ("QualifiedFunctionName", "<global>"),
                    ("Library", state.Artifact.Name))));
            state.Builder.AddEdge(new EvidenceEdge(state.FileId, functionId, "defines-function", EvidenceConfidence.Confirmed));
        }

        state.Builder.AddEdge(new EvidenceEdge(functionId, behaviorId, "performs", confidence));
    }

    private static IEnumerable<string> FindOperationNames(Node node)
    {
        if (node is Property property &&
            string.Equals(GetPropertyName(property.Key), "operationName", StringComparison.OrdinalIgnoreCase) &&
            GetLiteralString(property.Value) is { Length: > 0 } operationName)
        {
            yield return operationName;
        }

        foreach (var child in node.ChildNodes)
        {
            if (child is null)
            {
                continue;
            }

            foreach (var value in FindOperationNames(child))
            {
                yield return value;
            }
        }
    }

    private static bool TryGetBoundField(Expression receiver, string getterName, out string field)
    {
        if (receiver is CallExpression getterCall &&
            getterCall.Callee is MemberExpression getterMember &&
            string.Equals(GetPropertyName(getterMember.Property), getterName, StringComparison.OrdinalIgnoreCase) &&
            GetLiteralString(ArgumentAt(getterCall, 0)) is { Length: > 0 } name)
        {
            field = name;
            return true;
        }

        field = string.Empty;
        return false;
    }

    private static Node? ArgumentAt(CallExpression call, int index) =>
        index >= 0 && index < call.Arguments.Count ? call.Arguments[index] : null;

    private static string? GetLiteralString(Node? node) => node switch
    {
        Literal { StringValue: { } value } => value,
        Literal { BooleanValue: { } value } => value ? "true" : "false",
        Literal { NumericValue: { } value } => value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        TemplateLiteral template when template.Expressions.Count == 0 =>
            string.Concat(template.Quasis.Select(quasi => quasi.Value.Cooked)),
        _ => null
    };

    private static IReadOnlyList<string> GetObjectFields(Node? node)
    {
        if (node is not ObjectExpression objectExpression)
        {
            return [];
        }

        var fields = new List<string>();
        foreach (var propertyNode in objectExpression.Properties)
        {
            if (propertyNode is Property property && GetPropertyName(property.Key) is { Length: > 0 } name)
            {
                fields.Add(name);
            }
        }

        return fields.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string? GetObjectString(Node? node, string propertyName)
    {
        if (node is not ObjectExpression objectExpression) return null;
        foreach (var propertyNode in objectExpression.Properties)
        {
            if (propertyNode is Property property &&
                string.Equals(GetPropertyName(property.Key), propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return GetLiteralString(property.Value);
            }
        }
        return null;
    }

    private static string? GetExpressionPath(Node? expression) => expression switch
    {
        Identifier identifier => identifier.Name,
        ThisExpression => "this",
        Literal literal => literal.StringValue,
        MemberExpression member => JoinPath(GetExpressionPath(member.Object), GetPropertyName(member.Property)),
        ChainExpression chain => GetExpressionPath(chain.Expression),
        _ => null
    };

    private static string? GetPropertyName(Node? property) => property switch
    {
        Identifier identifier => identifier.Name,
        Literal literal => literal.StringValue ?? literal.Value?.ToString(),
        _ => null
    };

    private static string? JoinPath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left))
        {
            return right;
        }

        return string.IsNullOrWhiteSpace(right) ? left : $"{left}.{right}";
    }

    private static string? GetArgumentSource(CallExpression call, int index, string source)
    {
        var argument = ArgumentAt(call, index);
        if (argument is null || argument.Range.Start < 0 || argument.Range.End < argument.Range.Start ||
            argument.Range.End > source.Length)
        {
            return null;
        }

        return Truncate(source[argument.Range.Start..argument.Range.End].Trim(), 160);
    }

    private static string ArgumentSuffix(CallExpression call, int index, string source)
    {
        var value = GetArgumentSource(call, index, source);
        return string.IsNullOrWhiteSpace(value) ? string.Empty : $"（表达式：{value}）";
    }

    private static string NodeLocation(Node node, string description) =>
        node.Location.Start.Line > 0
            ? $"line {node.Location.Start.Line}, column {node.Location.Start.Column}: {description}"
            : description;

    private static string FunctionId(string artifactName, string functionName) =>
        EvidenceId.Create("js-function", artifactName, functionName);

    private static string BehaviorLabel(string behavior, string? subject) =>
        string.IsNullOrWhiteSpace(subject) ? behavior : $"{behavior}: {subject}";

    private static string TranslateMethod(string method) => method == "addOnChange" ? "注册" : "移除";

    private static bool LooksLikeDataverseOperationUrl(string url) =>
        url.Contains("/api/data/", StringComparison.OrdinalIgnoreCase) &&
        (url.Contains("Microsoft.Dynamics.CRM.", StringComparison.OrdinalIgnoreCase) ||
         url.Contains("new_", StringComparison.OrdinalIgnoreCase));

    private static string? ExtractOperationFromUrl(string url)
    {
        var clean = url.Split('?', '#')[0].TrimEnd('/');
        var slash = clean.LastIndexOf('/');
        var segment = slash >= 0 ? clean[(slash + 1)..] : clean;
        var marker = "Microsoft.Dynamics.CRM.";
        var markerIndex = segment.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0)
        {
            return segment[(markerIndex + marker.Length)..].TrimEnd(')');
        }

        var parenthesis = segment.LastIndexOf(')');
        if (parenthesis >= 0 && parenthesis + 1 < segment.Length)
        {
            return segment[(parenthesis + 1)..].TrimStart('/');
        }

        return segment.StartsWith("new_", StringComparison.OrdinalIgnoreCase) ? segment : null;
    }

    private static bool HasSafeNesting(string source)
    {
        var depth = 0;
        var inSingle = false;
        var inDouble = false;
        var inTemplate = false;
        var escaped = false;
        foreach (var character in source)
        {
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if ((inSingle || inDouble || inTemplate) && character == '\\')
            {
                escaped = true;
                continue;
            }

            if (!inDouble && !inTemplate && character == '\'')
            {
                inSingle = !inSingle;
                continue;
            }

            if (!inSingle && !inTemplate && character == '"')
            {
                inDouble = !inDouble;
                continue;
            }

            if (!inSingle && !inDouble && character == '`')
            {
                inTemplate = !inTemplate;
                continue;
            }

            if (inSingle || inDouble || inTemplate)
            {
                continue;
            }

            if (character is '(' or '[' or '{')
            {
                if (++depth > MaxLexicalNesting)
                {
                    return false;
                }
            }
            else if (character is ')' or ']' or '}')
            {
                depth = Math.Max(0, depth - 1);
            }
        }

        return true;
    }

    private static string Truncate(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum] + "…";

    private sealed record WalkState(
        DecodedArtifact Artifact,
        string Source,
        EvidenceGraphBuilder Builder,
        string FileId,
        string[] OperationNames,
        CancellationToken CancellationToken);

    private sealed record FunctionContext(string Name, string QualifiedName);
}
