namespace CrmLogicLens.Core;

public sealed class AnalysisPipeline
{
    private readonly ArtifactDecoder _decoder;
    private readonly FormXmlAnalyzer _formAnalyzer;
    private readonly RibbonXmlAnalyzer _ribbonAnalyzer;
    private readonly JavaScriptAnalyzer _javaScriptAnalyzer;
    private readonly PluginCatalogAnalyzer _pluginCatalogAnalyzer;
    private readonly PluginAssemblyInspector _assemblyInspector;
    private readonly EntityMetadataAnalyzer _metadataAnalyzer;
    private readonly CSharpPluginAnalyzer _cSharpPluginAnalyzer;
    private readonly CustomPageCatalogAnalyzer _customPageCatalogAnalyzer = new();

    public AnalysisPipeline()
        : this(
            new ArtifactDecoder(),
            new FormXmlAnalyzer(),
            new RibbonXmlAnalyzer(),
            new JavaScriptAnalyzer(),
            new PluginCatalogAnalyzer(),
            new PluginAssemblyInspector(),
            new EntityMetadataAnalyzer(),
            new CSharpPluginAnalyzer())
    {
    }

    public AnalysisPipeline(
        ArtifactDecoder decoder,
        FormXmlAnalyzer formAnalyzer,
        RibbonXmlAnalyzer ribbonAnalyzer,
        JavaScriptAnalyzer javaScriptAnalyzer,
        PluginCatalogAnalyzer pluginCatalogAnalyzer,
        PluginAssemblyInspector assemblyInspector,
        EntityMetadataAnalyzer metadataAnalyzer)
        : this(decoder, formAnalyzer, ribbonAnalyzer, javaScriptAnalyzer, pluginCatalogAnalyzer,
            assemblyInspector, metadataAnalyzer, new CSharpPluginAnalyzer())
    {
    }

    public AnalysisPipeline(
        ArtifactDecoder decoder,
        FormXmlAnalyzer formAnalyzer,
        RibbonXmlAnalyzer ribbonAnalyzer,
        JavaScriptAnalyzer javaScriptAnalyzer,
        PluginCatalogAnalyzer pluginCatalogAnalyzer,
        PluginAssemblyInspector assemblyInspector,
        EntityMetadataAnalyzer metadataAnalyzer,
        CSharpPluginAnalyzer cSharpPluginAnalyzer)
    {
        _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        _formAnalyzer = formAnalyzer ?? throw new ArgumentNullException(nameof(formAnalyzer));
        _ribbonAnalyzer = ribbonAnalyzer ?? throw new ArgumentNullException(nameof(ribbonAnalyzer));
        _javaScriptAnalyzer = javaScriptAnalyzer ?? throw new ArgumentNullException(nameof(javaScriptAnalyzer));
        _pluginCatalogAnalyzer = pluginCatalogAnalyzer ?? throw new ArgumentNullException(nameof(pluginCatalogAnalyzer));
        _assemblyInspector = assemblyInspector ?? throw new ArgumentNullException(nameof(assemblyInspector));
        _metadataAnalyzer = metadataAnalyzer ?? throw new ArgumentNullException(nameof(metadataAnalyzer));
        _cSharpPluginAnalyzer = cSharpPluginAnalyzer ?? throw new ArgumentNullException(nameof(cSharpPluginAnalyzer));
    }

    public AnalysisResult Analyze(
        SnapshotUpload upload,
        CancellationToken cancellationToken = default,
        Guid? snapshotId = null)
    {
        ArgumentNullException.ThrowIfNull(upload);
        ArgumentNullException.ThrowIfNull(upload.Context);
        if (upload.Artifacts is null)
        {
            throw new ArtifactValidationException("Snapshot artifact collection is missing.");
        }

        if (upload.Artifacts.Count > _decoder.Options.MaxArtifactsPerSnapshot)
        {
            throw new ArtifactValidationException(
                $"Snapshot contains more than {_decoder.Options.MaxArtifactsPerSnapshot:N0} artifacts.");
        }

        var builder = new EvidenceGraphBuilder();
        AddPageContext(upload.Context, builder);
        var decodedArtifacts = new List<DecodedArtifact>(upload.Artifacts.Count);
        long decodedBytes = 0;
        foreach (var artifact in upload.Artifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (artifact is null)
            {
                builder.AddWarning("Snapshot contains a null artifact entry.");
                continue;
            }

            try
            {
                var decoded = _decoder.Decode(artifact);
                decodedBytes += decoded.Bytes.Length;
                if (decodedBytes > _decoder.Options.MaxSnapshotBytes)
                {
                    throw new ArtifactValidationException(
                        $"Decoded snapshot exceeds {_decoder.Options.MaxSnapshotBytes:N0} bytes.");
                }

                decodedArtifacts.Add(decoded);
            }
            catch (ArtifactValidationException exception)
            {
                builder.AddWarning($"{SafeName(artifact.Name)}: {exception.Message}");
            }
        }

        // Catalogs are analyzed before binaries so SourceType can inform the no-content probe.
        foreach (var artifact in decodedArtifacts.Where(value => value.Kind == ArtifactKind.PluginCatalog))
        {
            AnalyzeSafely(artifact, () => _pluginCatalogAnalyzer.Analyze(artifact, cancellationToken), builder);
        }

        foreach (var artifact in decodedArtifacts.Where(value => value.Kind != ArtifactKind.PluginCatalog &&
                                                                  value.Kind != ArtifactKind.PluginAssembly))
        {
            AnalyzeSafely(artifact, () => AnalyzeTextArtifact(artifact, cancellationToken), builder);
        }

        var sourceTypes = GetAssemblySourceTypes(builder.Nodes);
        var registeredTypes = GetRegisteredTypeNamesByAssembly(builder.Nodes);
        foreach (var artifact in decodedArtifacts.Where(value => value.Kind == ArtifactKind.PluginAssembly))
        {
            var key = EvidenceId.NormalizeExternal(artifact.ComponentId);
            sourceTypes.TryGetValue(key, out var sourceType);
            registeredTypes.TryGetValue(key, out var relevantTypeNames);
            AnalyzeSafely(
                artifact,
                () => _assemblyInspector.Analyze(
                    artifact,
                    sourceType,
                    relevantTypeNames ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    cancellationToken),
                builder);
        }

        LinkEvidence(builder, upload.Context, cancellationToken);
        var graph = builder.Build();
        return new AnalysisResult(
            snapshotId ?? Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            graph,
            BuildSummary(graph, decodedArtifacts.Count, upload.Artifacts.Count));
    }

    private EvidenceGraph AnalyzeTextArtifact(DecodedArtifact artifact, CancellationToken cancellationToken) =>
        artifact.Kind switch
        {
            ArtifactKind.FormXml => _formAnalyzer.Analyze(artifact, cancellationToken),
            ArtifactKind.RibbonXml => _ribbonAnalyzer.Analyze(artifact, cancellationToken),
            ArtifactKind.JavaScript => _javaScriptAnalyzer.Analyze(artifact, cancellationToken),
            ArtifactKind.EntityMetadata => _metadataAnalyzer.Analyze(artifact, cancellationToken),
            ArtifactKind.CustomPageCatalog => _customPageCatalogAnalyzer.Analyze(artifact, cancellationToken),
            ArtifactKind.DecompiledCSharp => _cSharpPluginAnalyzer.Analyze(artifact, cancellationToken),
            _ => new EvidenceGraph([], [], [$"{artifact.Name}: unsupported artifact kind {artifact.Kind}."])
        };

    private static void AnalyzeSafely(
        DecodedArtifact artifact,
        Func<EvidenceGraph> analyze,
        EvidenceGraphBuilder builder)
    {
        try
        {
            builder.Merge(analyze());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            builder.AddWarning($"{SafeName(artifact.Name)}: analyzer rejected the artifact ({exception.Message}).");
        }
    }

    private static void AddPageContext(CrmPageContext context, EvidenceGraphBuilder builder)
    {
        var pageId = PageNodeId(context);
        var label = context.FormLabel ?? context.EntityName ?? context.PageType;
        builder.AddNode(new EvidenceNode(
            pageId,
            "PageContext",
            label,
            $"当前页面类型为 {context.PageType}，实体为 {context.EntityName ?? "未知"}，窗体为 {context.FormLabel ?? context.FormId ?? "未知"}。",
            "browser-context",
            "current page",
            EvidenceConfidence.Confirmed,
            EvidenceProperties.Create(
                ("OrganizationUrl", context.OrganizationUrl),
                ("OrganizationId", context.OrganizationId),
                ("Version", context.Version),
                ("ApiVersion", context.ApiVersion),
                ("PageType", context.PageType),
                ("Entity", context.EntityName),
                ("EntityId", context.EntityId),
                ("FormId", context.FormId),
                ("AppId", context.AppId),
                ("FormLabel", context.FormLabel))));
    }

    private static Dictionary<string, int?> GetAssemblySourceTypes(IEnumerable<EvidenceNode> nodes)
    {
        var result = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes.Where(node => node.Kind == "PluginAssembly" && node.Properties is not null))
        {
            if (!node.Properties!.TryGetValue("AssemblyId", out var id) || string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            node.Properties.TryGetValue("SourceType", out var sourceText);
            result[EvidenceId.NormalizeExternal(id)] = int.TryParse(sourceText, out var sourceType) ? sourceType : null;
        }

        return result;
    }

    private static Dictionary<string, HashSet<string>> GetRegisteredTypeNamesByAssembly(
        IEnumerable<EvidenceNode> nodes)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes.Where(node => node.Kind == "PluginType" && node.Properties is not null))
        {
            var assemblyId = GetProperty(node, "AssemblyId");
            var typeName = GetProperty(node, "TypeName");
            if (string.IsNullOrWhiteSpace(assemblyId) || string.IsNullOrWhiteSpace(typeName))
            {
                continue;
            }

            var key = EvidenceId.NormalizeExternal(assemblyId);
            if (!result.TryGetValue(key, out var names))
            {
                names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                result[key] = names;
            }
            names.Add(typeName);
        }
        return result;
    }

    private static void LinkEvidence(
        EvidenceGraphBuilder builder,
        CrmPageContext context,
        CancellationToken cancellationToken)
    {
        LinkPageContext(builder, context);
        LinkConfiguredHandlers(builder, "FormHandler", "implemented-by");
        LinkConfiguredHandlers(builder, "RibbonJavaScriptAction", "implemented-by");
        LinkOpenedCustomPages(builder);
        LinkJavaScriptToCustomApis(builder);
        LinkJavaScriptToPluginSteps(builder, cancellationToken);
        LinkPluginTypesToMetadata(builder);
        LinkPluginTypesToDecompiledSource(builder);
        LinkFieldMetadata(builder);
    }

    private static void LinkPageContext(EvidenceGraphBuilder builder, CrmPageContext context)
    {
        var pageId = PageNodeId(context);
        foreach (var form in builder.Nodes.Where(node => node.Kind == "Form"))
        {
            var confirmed = form.Properties?.TryGetValue("ComponentId", out var componentId) == true &&
                            SameComponent(componentId, context.FormId);
            builder.AddEdge(new EvidenceEdge(
                pageId,
                form.Id,
                confirmed ? "current-form" : "contains-form-artifact",
                confirmed ? EvidenceConfidence.Confirmed : EvidenceConfidence.Inferred));
        }

        foreach (var entity in builder.Nodes.Where(node => node.Kind == "EntityMetadata" &&
                                                            PropertyEquals(node, "Entity", context.EntityName)))
        {
            builder.AddEdge(new EvidenceEdge(pageId, entity.Id, "current-entity", EvidenceConfidence.Confirmed));
        }

        foreach (var filter in builder.Nodes.Where(node => node.Kind == "PluginFilter" &&
                                                            PropertyEquals(node, "Entity", context.EntityName)))
        {
            builder.AddEdge(new EvidenceEdge(pageId, filter.Id, "entity-plugin-filter", EvidenceConfidence.Confirmed));
        }
    }

    private static void LinkConfiguredHandlers(EvidenceGraphBuilder builder, string sourceKind, string relation)
    {
        var sources = builder.Nodes.Where(node => node.Kind == sourceKind).ToArray();
        var functions = builder.Nodes.Where(node => node.Kind == "JavaScriptFunction").ToArray();
        foreach (var source in sources)
        {
            var configuredFunction = GetProperty(source, "FunctionName");
            var configuredLibrary = NormalizeLibrary(GetProperty(source, "Library"));
            if (string.IsNullOrWhiteSpace(configuredFunction))
            {
                continue;
            }

            var candidates = functions.Where(function => FunctionMatches(function, configuredFunction)).ToArray();
            var libraryCandidates = candidates.Where(function =>
                LibraryMatches(configuredLibrary, NormalizeLibrary(GetProperty(function, "Library")))).ToArray();
            if (libraryCandidates.Length > 0)
            {
                candidates = libraryCandidates;
            }

            foreach (var function in candidates)
            {
                var exactName = string.Equals(
                    GetProperty(function, "QualifiedFunctionName"),
                    configuredFunction,
                    StringComparison.OrdinalIgnoreCase);
                var exactLibrary = !string.IsNullOrWhiteSpace(configuredLibrary) &&
                                   LibraryMatches(configuredLibrary, NormalizeLibrary(GetProperty(function, "Library")));
                builder.AddEdge(new EvidenceEdge(
                    source.Id,
                    function.Id,
                    relation,
                    exactName && exactLibrary ? EvidenceConfidence.Confirmed : EvidenceConfidence.Inferred));
            }
        }
    }

    private static void LinkOpenedCustomPages(EvidenceGraphBuilder builder)
    {
        var openings = builder.Nodes.Where(node =>
            node.Kind == "JavaScriptBehavior" &&
            PropertyEquals(node, "Behavior", "OpenCustomPage")).ToArray();
        var pages = builder.Nodes.Where(node => node.Kind == "CustomPage").ToArray();
        foreach (var opening in openings)
        {
            var target = NormalizeLibrary(GetProperty(opening, "TargetName"));
            if (string.IsNullOrWhiteSpace(target)) continue;
            foreach (var page in pages.Where(page =>
                         string.Equals(NormalizeLibrary(GetProperty(page, "PageName")), target,
                             StringComparison.OrdinalIgnoreCase)))
            {
                builder.AddEdge(new EvidenceEdge(
                    opening.Id,
                    page.Id,
                    "opens-custom-page",
                    EvidenceConfidence.Confirmed));
            }
        }
    }

    private static void LinkJavaScriptToPluginSteps(EvidenceGraphBuilder builder, CancellationToken cancellationToken)
    {
        var behaviors = builder.Nodes.Where(node => node.Kind is "JavaScriptBehavior" or "CSharpPluginBehavior").ToArray();
        var steps = builder.Nodes.Where(node => node.Kind == "PluginStep").ToArray();
        foreach (var behavior in behaviors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var operation = GetProperty(behavior, "Operation");
            var entity = GetProperty(behavior, "Entity") ?? GetProperty(behavior, "Subject");
            if (string.IsNullOrWhiteSpace(operation))
            {
                continue;
            }

            var changedFields = SplitCsv(GetProperty(behavior, "Fields"));
            foreach (var step in steps)
            {
                if (!bool.TryParse(GetProperty(step, "Enabled"), out var enabled) || !enabled ||
                    !string.Equals(GetProperty(step, "Message"), operation, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var stepEntity = GetProperty(step, "Entity");
                if (!string.IsNullOrWhiteSpace(entity) && !string.IsNullOrWhiteSpace(stepEntity) &&
                    !string.Equals(entity, stepEntity, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var filteringAttributes = SplitCsv(GetProperty(step, "FilteringAttributes"));
                if (operation.Equals("Update", StringComparison.OrdinalIgnoreCase) &&
                    filteringAttributes.Count > 0 && changedFields.Count > 0 &&
                    !filteringAttributes.Overlaps(changedFields))
                {
                    continue;
                }

                builder.AddEdge(new EvidenceEdge(
                    behavior.Id,
                    step.Id,
                    "may-trigger-plugin-step",
                    EvidenceConfidence.Inferred));
            }
        }
    }

    private static void LinkJavaScriptToCustomApis(EvidenceGraphBuilder builder)
    {
        var behaviors = builder.Nodes.Where(node =>
            node.Kind == "JavaScriptBehavior" &&
            PropertyEquals(node, "Behavior", "CustomApiAction")).ToArray();
        var customApis = builder.Nodes.Where(node => node.Kind == "CustomApi").ToArray();
        foreach (var behavior in behaviors)
        {
            var operation = GetProperty(behavior, "Operation");
            var route = GetProperty(behavior, "Route");
            foreach (var api in customApis.Where(api =>
                         PropertyEquals(api, "Operation", operation) &&
                         (string.IsNullOrWhiteSpace(route) ||
                          string.IsNullOrWhiteSpace(GetProperty(api, "Route")) ||
                          PropertyEquals(api, "Route", route))))
            {
                builder.AddEdge(new EvidenceEdge(
                    behavior.Id,
                    api.Id,
                    "invokes-custom-api",
                    EvidenceConfidence.Confirmed));
            }
        }
    }

    private static void LinkPluginTypesToMetadata(EvidenceGraphBuilder builder)
    {
        var registeredTypes = builder.Nodes.Where(node => node.Kind == "PluginType").ToArray();
        var managedTypes = builder.Nodes.Where(node => node.Kind == "ManagedType").ToArray();
        foreach (var registeredType in registeredTypes)
        {
            var typeName = GetProperty(registeredType, "TypeName");
            var assemblyId = GetProperty(registeredType, "AssemblyId");
            foreach (var managedType in managedTypes.Where(node =>
                         PropertyEquals(node, "TypeName", typeName) &&
                         (string.IsNullOrWhiteSpace(assemblyId) || PropertyEquals(node, "AssemblyId", assemblyId))))
            {
                builder.AddEdge(new EvidenceEdge(
                    registeredType.Id,
                    managedType.Id,
                    "matches-managed-type",
                    EvidenceConfidence.Confirmed));
            }
        }
    }

    private static void LinkPluginTypesToDecompiledSource(EvidenceGraphBuilder builder)
    {
        var registeredTypes = builder.Nodes.Where(node => node.Kind == "PluginType").ToArray();
        var decompiledTypes = builder.Nodes.Where(node => node.Kind == "CSharpPluginType").ToArray();
        foreach (var registeredType in registeredTypes)
        {
            var registeredName = GetProperty(registeredType, "TypeName");
            var assemblyId = GetProperty(registeredType, "AssemblyId");
            foreach (var decompiledType in decompiledTypes)
            {
                var decompiledName = GetProperty(decompiledType, "TypeName");
                if (string.IsNullOrWhiteSpace(registeredName) || string.IsNullOrWhiteSpace(decompiledName) ||
                    !(string.Equals(registeredName, decompiledName, StringComparison.OrdinalIgnoreCase) ||
                      registeredName.EndsWith($".{decompiledName}", StringComparison.OrdinalIgnoreCase) ||
                      decompiledName.EndsWith($".{registeredName}", StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(assemblyId) && !PropertyEquals(decompiledType, "AssemblyId", assemblyId)))
                {
                    continue;
                }

                builder.AddEdge(new EvidenceEdge(
                    registeredType.Id,
                    decompiledType.Id,
                    "matches-decompiled-type",
                    EvidenceConfidence.Confirmed));
            }
        }
    }

    private static void LinkFieldMetadata(EvidenceGraphBuilder builder)
    {
        var fields = builder.Nodes.Where(node => node.Kind == "Field").ToArray();
        var metadata = builder.Nodes.Where(node => node.Kind == "FieldMetadata").ToArray();
        foreach (var field in fields)
        {
            var logicalName = GetProperty(field, "Field");
            foreach (var match in metadata.Where(node => PropertyEquals(node, "Field", logicalName)))
            {
                builder.AddEdge(new EvidenceEdge(field.Id, match.Id, "described-by", EvidenceConfidence.Confirmed));
            }
        }
    }

    private static bool FunctionMatches(EvidenceNode function, string configuredName)
    {
        var simpleName = GetProperty(function, "FunctionName");
        var qualifiedName = GetProperty(function, "QualifiedFunctionName");
        return string.Equals(simpleName, configuredName, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(qualifiedName, configuredName, StringComparison.OrdinalIgnoreCase) ||
               configuredName.EndsWith($".{simpleName}", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LibraryMatches(string? configured, string? actual)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(actual))
        {
            return false;
        }

        return string.Equals(configured, actual, StringComparison.OrdinalIgnoreCase) ||
               configured.EndsWith(actual, StringComparison.OrdinalIgnoreCase) ||
               actual.EndsWith(configured, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeLibrary(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        const string prefix = "$webresource:";
        var result = value.Trim().Replace('\\', '/');
        if (result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            result = result[prefix.Length..];
        }

        return result.TrimStart('/').ToLowerInvariant();
    }

    private static HashSet<string> SplitCsv(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string? GetProperty(EvidenceNode node, string name) =>
        node.Properties?.TryGetValue(name, out var value) == true ? value : null;

    private static bool PropertyEquals(EvidenceNode node, string name, string? expected) =>
        !string.IsNullOrWhiteSpace(expected) &&
        string.Equals(GetProperty(node, name), expected, StringComparison.OrdinalIgnoreCase);

    private static bool SameComponent(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(second) &&
        string.Equals(EvidenceId.NormalizeExternal(first), EvidenceId.NormalizeExternal(second),
            StringComparison.OrdinalIgnoreCase);

    private static string PageNodeId(CrmPageContext context) =>
        EvidenceId.Create("page", context.OrganizationUrl, context.AppId, context.EntityName, context.EntityId, context.FormId);

    private static string BuildSummary(EvidenceGraph graph, int acceptedArtifacts, int uploadedArtifacts)
    {
        static int Count(EvidenceGraph value, string kind) => value.Nodes.Count(node => node.Kind == kind);

        return $"已接受并静态分析 {acceptedArtifacts}/{uploadedArtifacts} 个工件；" +
               $"识别 {Count(graph, "FormEvent")} 个窗体事件、{Count(graph, "RibbonCommand")} 个命令栏动作、" +
               $"{Count(graph, "JavaScriptBehavior")} 个前端行为、{Count(graph, "PluginStep")} 个当前实体插件步骤、" +
               $"{Count(graph, "PluginAssembly")} 个相关插件程序集。插件源码由 AI 按步骤需要读取。" +
               (graph.Warnings.Count == 0 ? string.Empty : $" 有 {graph.Warnings.Count} 项边界或输入告警。");
    }

    private static string SafeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "artifact";
        }

        var sanitized = new string(name.Where(character => !char.IsControl(character)).Take(200).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "artifact" : sanitized;
    }
}
