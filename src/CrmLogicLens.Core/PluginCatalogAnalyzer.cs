using System.Text.Json;

namespace CrmLogicLens.Core;

public sealed class PluginCatalogAnalyzer
{
    public EvidenceGraph Analyze(DecodedArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var builder = new EvidenceGraphBuilder();
        if (artifact.Kind != ArtifactKind.PluginCatalog || artifact.Text is null)
        {
            builder.AddWarning($"{artifact.Name}: artifact is not plugin-catalog JSON text.");
            return builder.Build();
        }

        try
        {
            using var document = JsonDocument.Parse(artifact.Text, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 48
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                builder.AddWarning($"{artifact.Name}: plugin catalog root must be a JSON object.");
                return builder.Build();
            }

            var catalog = ReadCatalog(document.RootElement, cancellationToken);
            AddAssemblies(catalog, artifact, builder);
            AddTypes(catalog, artifact, builder);
            AddCustomApis(catalog, artifact, builder);
            AddMessages(catalog, artifact, builder);
            AddFilters(catalog, artifact, builder);
            AddSteps(catalog, artifact, builder);
            AddMissingReferenceWarnings(catalog, artifact, builder);
        }
        catch (JsonException exception)
        {
            builder.AddWarning($"{artifact.Name}: invalid plugin catalog JSON ({exception.Message}).");
        }
        catch (InvalidOperationException exception)
        {
            builder.AddWarning($"{artifact.Name}: plugin catalog could not be analyzed ({exception.Message}).");
        }

        return builder.Build();
    }

    private static PluginCatalog ReadCatalog(JsonElement root, CancellationToken cancellationToken)
    {
        var steps = ReadArray(root, "steps", (element, index) => new PluginStep(
            NormalizeId(GetText(element, "id"), "step", index),
            GetText(element, "name") ?? $"Plugin step {index + 1}",
            EvidenceId.NormalizeExternal(GetText(element, "messageId")),
            EvidenceId.NormalizeExternal(GetText(element, "filterId")),
            EvidenceId.NormalizeExternal(GetText(element, "eventHandlerId")),
            GetInt(element, "stage"),
            GetInt(element, "mode"),
            GetInt(element, "rank"),
            NormalizeCsv(GetText(element, "filteringAttributes")),
            GetInt(element, "stateCode")), cancellationToken);

        var messages = ReadArray(root, "messages", (element, index) => new PluginMessage(
            NormalizeId(GetText(element, "id"), "message", index),
            GetText(element, "name") ?? $"Message {index + 1}"), cancellationToken);

        var filters = ReadArray(root, "filters", (element, index) => new PluginFilter(
            NormalizeId(GetText(element, "id"), "filter", index),
            GetText(element, "primaryObjectTypeCode"),
            GetText(element, "secondaryObjectTypeCode")), cancellationToken);

        var types = ReadArray(root, "types", (element, index) => new PluginType(
            NormalizeId(GetText(element, "id"), "type", index),
            GetText(element, "typeName") ?? GetText(element, "name") ?? $"Plugin type {index + 1}",
            GetText(element, "name"),
            EvidenceId.NormalizeExternal(GetText(element, "assemblyId"))), cancellationToken);

        var assemblies = ReadArray(root, "assemblies", (element, index) => new PluginAssembly(
            NormalizeId(GetText(element, "id"), "assembly", index),
            GetText(element, "name") ?? $"Plugin assembly {index + 1}",
            GetText(element, "version"),
            GetInt(element, "sourceType"),
            GetInt(element, "isolationMode"),
            GetText(element, "path"),
            GetText(element, "sourceHash")), cancellationToken);

        var customApis = ReadArray(root, "customApis", (element, index) => new CustomApi(
            NormalizeId(GetText(element, "id"), "custom-api", index),
            GetText(element, "definitionId"),
            GetText(element, "messageId"),
            GetText(element, "uniqueName") ?? GetText(element, "name") ?? $"Custom API {index + 1}",
            GetText(element, "name"),
            GetText(element, "route"),
            GetText(element, "library"),
            GetInt(element, "bindingType"),
            GetText(element, "boundEntityLogicalName"),
            GetBool(element, "isFunction"),
            GetBool(element, "isPrivate"),
            EvidenceId.NormalizeExternal(GetText(element, "pluginTypeId")),
            GetText(element, "source")), cancellationToken);

        return new PluginCatalog(steps, messages, filters, types, assemblies, customApis);
    }

    private static void AddAssemblies(PluginCatalog catalog, DecodedArtifact artifact, EvidenceGraphBuilder builder)
    {
        foreach (var assembly in catalog.Assemblies)
        {
            var isDisk = assembly.SourceType == 1;
            var sourceDescription = assembly.SourceType switch
            {
                0 => "Database",
                1 => "Disk",
                null => "未知",
                _ => $"SourceType={assembly.SourceType}"
            };
            var summary = isDisk
                ? $"插件程序集 {assembly.Name} {assembly.Version} 采用磁盘部署；目录数据不包含可反编译的 DLL。"
                : $"插件程序集 {assembly.Name} {assembly.Version}，部署来源 {sourceDescription}。";
            var id = AssemblyNodeId(artifact.Name, assembly.Id);
            builder.AddNode(new EvidenceNode(
                id,
                "PluginAssembly",
                assembly.Name,
                summary,
                artifact.Name,
                $"assemblies/{assembly.Id}",
                EvidenceConfidence.Confirmed,
                EvidenceProperties.Create(
                    ("AssemblyId", assembly.Id),
                    ("Version", assembly.Version),
                    ("SourceType", assembly.SourceType),
                    ("SourceTypeName", sourceDescription),
                    ("IsolationMode", assembly.IsolationMode),
                    ("Path", assembly.Path),
                    ("SourceHash", assembly.SourceHash),
                    ("ContentAvailability", isDisk ? "UnavailableWithoutCrmServerFileAccess" : "DatabaseContentMayBeAvailable"))));
            if (isDisk)
            {
                builder.AddWarning($"{assembly.Name}: SourceType=Disk，当前权限无法从 CRM 服务器磁盘读取 DLL。 ");
            }
        }
    }

    private static void AddTypes(PluginCatalog catalog, DecodedArtifact artifact, EvidenceGraphBuilder builder)
    {
        foreach (var type in catalog.Types)
        {
            var id = TypeNodeId(artifact.Name, type.Id);
            builder.AddNode(new EvidenceNode(
                id,
                "PluginType",
                type.TypeName,
                $"已注册插件类型 {type.TypeName}。",
                artifact.Name,
                $"types/{type.Id}",
                EvidenceConfidence.Confirmed,
                EvidenceProperties.Create(
                    ("PluginTypeId", type.Id),
                    ("TypeName", type.TypeName),
                    ("Name", type.Name),
                    ("AssemblyId", type.AssemblyId))));
            if (!string.IsNullOrWhiteSpace(type.AssemblyId))
            {
                builder.AddEdge(new EvidenceEdge(
                    id,
                    AssemblyNodeId(artifact.Name, type.AssemblyId),
                    "implemented-in",
                    EvidenceConfidence.Confirmed));
            }
        }
    }

    private static void AddCustomApis(PluginCatalog catalog, DecodedArtifact artifact, EvidenceGraphBuilder builder)
    {
        foreach (var api in catalog.CustomApis)
        {
            var nodeId = CustomApiNodeId(api.Id);
            var routeText = string.IsNullOrWhiteSpace(api.Route) ? string.Empty : $"；业务路由 {api.Route}";
            var implementation = string.IsNullOrWhiteSpace(api.PluginTypeId)
                ? "尚未解析到实现插件类型"
                : "已解析到实现插件类型";
            builder.AddNode(new EvidenceNode(
                nodeId,
                "CustomApi",
                api.UniqueName,
                $"前端调用 Dataverse 自定义 API/Action {api.UniqueName}{routeText}；{implementation}。",
                artifact.Name,
                $"customApis/{api.Id}",
                EvidenceConfidence.Confirmed,
                EvidenceProperties.Create(
                    ("CustomApiId", api.DefinitionId),
                    ("MessageId", api.MessageId),
                    ("Operation", api.UniqueName),
                    ("Name", api.Name),
                    ("Route", api.Route),
                    ("Library", api.Library),
                    ("BindingType", api.BindingType),
                    ("BoundEntity", api.BoundEntityLogicalName),
                    ("IsFunction", api.IsFunction),
                    ("IsPrivate", api.IsPrivate),
                    ("PluginTypeId", api.PluginTypeId),
                    ("DefinitionSource", api.Source))));
            if (!string.IsNullOrWhiteSpace(api.PluginTypeId))
            {
                builder.AddEdge(new EvidenceEdge(
                    nodeId,
                    TypeNodeId(artifact.Name, api.PluginTypeId),
                    "implemented-by",
                    EvidenceConfidence.Confirmed));
            }
        }
    }

    private static void AddMessages(PluginCatalog catalog, DecodedArtifact artifact, EvidenceGraphBuilder builder)
    {
        foreach (var message in catalog.Messages)
        {
            builder.AddNode(new EvidenceNode(
                MessageNodeId(artifact.Name, message.Id),
                "PluginMessage",
                message.Name,
                $"Dataverse 消息 {message.Name}。",
                artifact.Name,
                $"messages/{message.Id}",
                EvidenceConfidence.Confirmed,
                EvidenceProperties.Create(("MessageId", message.Id), ("Message", message.Name))));
        }
    }

    private static void AddFilters(PluginCatalog catalog, DecodedArtifact artifact, EvidenceGraphBuilder builder)
    {
        foreach (var filter in catalog.Filters)
        {
            var label = filter.PrimaryObjectTypeCode ?? "未指定实体";
            builder.AddNode(new EvidenceNode(
                FilterNodeId(artifact.Name, filter.Id),
                "PluginFilter",
                label,
                $"插件消息筛选实体 {label}。",
                artifact.Name,
                $"filters/{filter.Id}",
                EvidenceConfidence.Confirmed,
                EvidenceProperties.Create(
                    ("FilterId", filter.Id),
                    ("Entity", filter.PrimaryObjectTypeCode),
                    ("SecondaryEntity", filter.SecondaryObjectTypeCode))));
        }
    }

    private static void AddSteps(PluginCatalog catalog, DecodedArtifact artifact, EvidenceGraphBuilder builder)
    {
        var messageById = catalog.Messages.GroupBy(value => value.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var filterById = catalog.Filters.GroupBy(value => value.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var typeById = catalog.Types.GroupBy(value => value.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var step in catalog.Steps)
        {
            messageById.TryGetValue(step.MessageId, out var message);
            filterById.TryGetValue(step.FilterId, out var filter);
            typeById.TryGetValue(step.EventHandlerId, out var type);
            var messageName = message?.Name ?? "未知消息";
            var entityName = filter?.PrimaryObjectTypeCode ?? "未指定实体";
            var mode = step.Mode == 1 ? "异步" : step.Mode == 0 ? "同步" : "模式未知";
            var enabled = step.StateCode is null or 0;
            var stage = StageName(step.Stage);
            var filteringText = step.FilteringAttributes.Count == 0
                ? "未限制过滤字段"
                : $"过滤字段 {string.Join(", ", step.FilteringAttributes)}";
            var stepId = StepNodeId(artifact.Name, step.Id);
            builder.AddNode(new EvidenceNode(
                stepId,
                "PluginStep",
                step.Name,
                $"{(enabled ? "已启用" : "已禁用")}的{mode}插件步骤：在 {entityName} 的 {messageName} {stage}执行；{filteringText}。",
                artifact.Name,
                $"steps/{step.Id}",
                EvidenceConfidence.Confirmed,
                EvidenceProperties.Create(
                    ("StepId", step.Id),
                    ("MessageId", step.MessageId),
                    ("Message", message?.Name),
                    ("FilterId", step.FilterId),
                    ("Entity", filter?.PrimaryObjectTypeCode),
                    ("EventHandlerId", step.EventHandlerId),
                    ("PluginType", type?.TypeName),
                    ("AssemblyId", type?.AssemblyId),
                    ("Stage", step.Stage),
                    ("StageName", stage),
                    ("Mode", step.Mode),
                    ("ModeName", mode),
                    ("Rank", step.Rank),
                    ("FilteringAttributes", string.Join(",", step.FilteringAttributes)),
                    ("StateCode", step.StateCode),
                    ("Enabled", enabled))));

            if (!string.IsNullOrWhiteSpace(step.MessageId))
            {
                builder.AddEdge(new EvidenceEdge(stepId, MessageNodeId(artifact.Name, step.MessageId),
                    "handles-message", EvidenceConfidence.Confirmed));
            }

            if (!string.IsNullOrWhiteSpace(step.FilterId))
            {
                builder.AddEdge(new EvidenceEdge(stepId, FilterNodeId(artifact.Name, step.FilterId),
                    "targets-entity", EvidenceConfidence.Confirmed));
            }

            if (!string.IsNullOrWhiteSpace(step.EventHandlerId))
            {
                builder.AddEdge(new EvidenceEdge(stepId, TypeNodeId(artifact.Name, step.EventHandlerId),
                    "handled-by", EvidenceConfidence.Confirmed));
            }
        }
    }

    private static void AddMissingReferenceWarnings(PluginCatalog catalog, DecodedArtifact artifact, EvidenceGraphBuilder builder)
    {
        var messageIds = catalog.Messages.Select(value => value.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var filterIds = catalog.Filters.Select(value => value.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var typeIds = catalog.Types.Select(value => value.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var assemblyIds = catalog.Assemblies.Select(value => value.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var step in catalog.Steps)
        {
            if (!string.IsNullOrWhiteSpace(step.MessageId) && !messageIds.Contains(step.MessageId))
            {
                builder.AddWarning($"{artifact.Name}: step {step.Name} references a missing message {step.MessageId}.");
            }

            if (!string.IsNullOrWhiteSpace(step.FilterId) && !filterIds.Contains(step.FilterId))
            {
                builder.AddWarning($"{artifact.Name}: step {step.Name} references a missing filter {step.FilterId}.");
            }

            if (!string.IsNullOrWhiteSpace(step.EventHandlerId) && !typeIds.Contains(step.EventHandlerId))
            {
                builder.AddWarning($"{artifact.Name}: step {step.Name} references a missing plugin type {step.EventHandlerId}.");
            }
        }

        foreach (var type in catalog.Types.Where(type =>
                     !string.IsNullOrWhiteSpace(type.AssemblyId) && !assemblyIds.Contains(type.AssemblyId)))
        {
            builder.AddWarning($"{artifact.Name}: plugin type {type.TypeName} references a missing assembly {type.AssemblyId}.");
        }

        foreach (var api in catalog.CustomApis.Where(api =>
                     !string.IsNullOrWhiteSpace(api.PluginTypeId) && !typeIds.Contains(api.PluginTypeId)))
        {
            builder.AddWarning($"{artifact.Name}: custom API {api.UniqueName} references a missing plugin type {api.PluginTypeId}.");
        }
    }

    private static IReadOnlyList<T> ReadArray<T>(
        JsonElement root,
        string propertyName,
        Func<JsonElement, int, T> factory,
        CancellationToken cancellationToken)
    {
        if (!TryGetProperty(root, propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<T>();
        var index = 0;
        foreach (var element in property.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (element.ValueKind == JsonValueKind.Object)
            {
                result.Add(factory(element, index));
            }

            index++;
        }

        return result;
    }

    private static string? GetText(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static int? GetInt(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number)
            ? number
            : null;
    }

    private static bool? GetBool(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var result) => result,
            _ => null
        };
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string NormalizeId(string? id, string kind, int index)
    {
        var normalized = EvidenceId.NormalizeExternal(id);
        return string.IsNullOrWhiteSpace(normalized) ? $"missing-{kind}-{index}" : normalized;
    }

    private static IReadOnlyList<string> NormalizeCsv(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(field => field.ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

    private static string StageName(int? stage) => stage switch
    {
        10 => "预验证（Pre-validation）阶段",
        20 => "预操作（Pre-operation）阶段",
        40 => "后操作（Post-operation）阶段",
        null => "未知阶段",
        _ => $"阶段 {stage}"
    };

    // Dataverse component IDs are organization-wide GUIDs, so using them directly as the
    // canonical key lets catalog evidence merge with a separately uploaded DLL artifact.
    private static string StepNodeId(string artifactName, string id) => EvidenceId.Create("plugin-step", id);
    private static string MessageNodeId(string artifactName, string id) => EvidenceId.Create("plugin-message", id);
    private static string FilterNodeId(string artifactName, string id) => EvidenceId.Create("plugin-filter", id);
    private static string TypeNodeId(string artifactName, string id) => EvidenceId.Create("plugin-type", id);
    private static string AssemblyNodeId(string artifactName, string id) => EvidenceId.Create("plugin-assembly", id);
    private static string CustomApiNodeId(string id) => EvidenceId.Create("custom-api", id);

    private sealed record PluginCatalog(
        IReadOnlyList<PluginStep> Steps,
        IReadOnlyList<PluginMessage> Messages,
        IReadOnlyList<PluginFilter> Filters,
        IReadOnlyList<PluginType> Types,
        IReadOnlyList<PluginAssembly> Assemblies,
        IReadOnlyList<CustomApi> CustomApis);

    private sealed record PluginStep(
        string Id,
        string Name,
        string MessageId,
        string FilterId,
        string EventHandlerId,
        int? Stage,
        int? Mode,
        int? Rank,
        IReadOnlyList<string> FilteringAttributes,
        int? StateCode);

    private sealed record PluginMessage(string Id, string Name);
    private sealed record PluginFilter(string Id, string? PrimaryObjectTypeCode, string? SecondaryObjectTypeCode);
    private sealed record PluginType(string Id, string TypeName, string? Name, string AssemblyId);
    private sealed record PluginAssembly(
        string Id,
        string Name,
        string? Version,
        int? SourceType,
        int? IsolationMode,
        string? Path,
        string? SourceHash);

    private sealed record CustomApi(
        string Id,
        string? DefinitionId,
        string? MessageId,
        string UniqueName,
        string? Name,
        string? Route,
        string? Library,
        int? BindingType,
        string? BoundEntityLogicalName,
        bool? IsFunction,
        bool? IsPrivate,
        string PluginTypeId,
        string? Source);
}
