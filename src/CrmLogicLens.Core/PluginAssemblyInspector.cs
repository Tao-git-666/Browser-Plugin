using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace CrmLogicLens.Core;

public sealed record PluginAssemblyInspectorOptions
{
    public int MaxTypes { get; init; } = 20_000;
    public int MaxMethods { get; init; } = 150_000;
    public int MaxMetadataNameLength { get; init; } = 2_048;
}

/// <summary>
/// Reads PE/CLR metadata only. It deliberately never calls Assembly.Load or executes IL.
/// </summary>
public sealed class PluginAssemblyInspector
{
    private readonly PluginAssemblyInspectorOptions _options;

    public PluginAssemblyInspector() : this(new PluginAssemblyInspectorOptions())
    {
    }

    public PluginAssemblyInspector(PluginAssemblyInspectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxTypes <= 0 || options.MaxMethods <= 0 || options.MaxMetadataNameLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Metadata limits must be positive.");
        }

        _options = options;
    }

    public EvidenceGraph Analyze(DecodedArtifact artifact, CancellationToken cancellationToken = default) =>
        Analyze(artifact, sourceType: null, cancellationToken);

    public EvidenceGraph Analyze(
        DecodedArtifact artifact,
        int? sourceType,
        CancellationToken cancellationToken = default) =>
        Analyze(artifact, sourceType, relevantTypeNames: null, cancellationToken);

    public EvidenceGraph Analyze(
        DecodedArtifact artifact,
        int? sourceType,
        IReadOnlySet<string>? relevantTypeNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var builder = new EvidenceGraphBuilder();
        if (artifact.Kind != ArtifactKind.PluginAssembly)
        {
            builder.AddWarning($"{artifact.Name}: artifact is not a plugin assembly.");
            return builder.Build();
        }

        var assemblyKey = EvidenceId.NormalizeExternal(artifact.ComponentId);
        if (string.IsNullOrWhiteSpace(assemblyKey))
        {
            assemblyKey = artifact.Name;
        }

        var assemblyNodeId = EvidenceId.Create("plugin-assembly", assemblyKey);
        if (artifact.Bytes.IsEmpty)
        {
            var disk = sourceType == 1;
            builder.AddNode(new EvidenceNode(
                assemblyNodeId,
                "PluginAssembly",
                artifact.Name,
                disk
                    ? "该程序集采用磁盘部署，目录快照没有 DLL 内容；需要 CRM 服务器文件访问权限或由管理员另行提供。"
                    : "该程序集快照没有 DLL 内容，无法读取托管类型和方法。",
                artifact.Name,
                "PE metadata",
                EvidenceConfidence.Confirmed,
                EvidenceProperties.Create(
                    ("AssemblyId", artifact.ComponentId),
                    ("SourceType", sourceType),
                    ("ContentAvailable", false),
                    ("InspectionMode", "MetadataOnly"))));
            builder.AddWarning(disk
                ? $"{artifact.Name}: SourceType=Disk and no DLL bytes were supplied."
                : $"{artifact.Name}: no DLL bytes were supplied.");
            return builder.Build();
        }

        try
        {
            // PEReader is a metadata parser. No runtime assembly is created from these bytes.
            using var stream = new MemoryStream(artifact.Bytes.ToArray(), writable: false);
            using var peReader = new PEReader(stream, PEStreamOptions.PrefetchMetadata);
            if (!peReader.HasMetadata || peReader.PEHeaders.CorHeader is null)
            {
                builder.AddNode(new EvidenceNode(
                    assemblyNodeId,
                    "PluginAssembly",
                    artifact.Name,
                    "文件是 PE 或原生二进制，但不包含可读取的 CLR 元数据。",
                    artifact.Name,
                    "PE headers",
                    EvidenceConfidence.Confirmed,
                    EvidenceProperties.Create(
                        ("AssemblyId", artifact.ComponentId),
                        ("Managed", false),
                        ("ContentAvailable", true),
                        ("InspectionMode", "MetadataOnly"))));
                builder.AddWarning($"{artifact.Name}: PE file does not contain CLR metadata.");
                return builder.Build();
            }

            var metadata = peReader.GetMetadataReader(MetadataReaderOptions.ApplyWindowsRuntimeProjections);
            var metadataName = GetAssemblyName(metadata) ?? artifact.Name;
            var contentHash = Convert.ToHexString(SHA256.HashData(artifact.Bytes.Span)).ToLowerInvariant();
            builder.AddNode(new EvidenceNode(
                assemblyNodeId,
                "PluginAssembly",
                metadataName,
                $"已安全读取托管程序集 {metadataName} 的 PE/CLR 元数据（未加载、未执行 DLL）。",
                artifact.Name,
                "PE/CLR metadata",
                EvidenceConfidence.Confirmed,
                EvidenceProperties.Create(
                    ("AssemblyId", artifact.ComponentId),
                    ("Version", artifact.Version),
                    ("SourceType", sourceType),
                    ("Managed", true),
                    ("ContentAvailable", true),
                    ("InspectionMode", "MetadataOnly"),
                    ("Sha256", contentHash))));

            var typeCount = 0;
            var methodCount = 0L;
            var exposedTypeCount = 0;
            var inventoryComplete = true;
            foreach (var typeHandle in metadata.TypeDefinitions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++typeCount > _options.MaxTypes)
                {
                    builder.AddWarning($"{artifact.Name}: type limit {_options.MaxTypes:N0} reached; remaining metadata was not listed.");
                    inventoryComplete = false;
                    break;
                }

                var typeDefinition = metadata.GetTypeDefinition(typeHandle);
                var typeName = ReadName(metadata, typeDefinition.Name);
                if (typeName == "<Module>")
                {
                    continue;
                }

                var typeNamespace = ReadName(metadata, typeDefinition.Namespace);
                var fullName = string.IsNullOrWhiteSpace(typeNamespace) ? typeName : $"{typeNamespace}.{typeName}";
                var declaredMethodCount = typeDefinition.GetMethods().Count;
                methodCount += declaredMethodCount;
                if (methodCount > _options.MaxMethods)
                {
                    inventoryComplete = false;
                }

                if (relevantTypeNames is not null && !relevantTypeNames.Contains(fullName))
                {
                    continue;
                }

                exposedTypeCount++;
                var typeNodeId = EvidenceId.Create("managed-type", assemblyKey, fullName);
                builder.AddNode(new EvidenceNode(
                    typeNodeId,
                    "ManagedType",
                    fullName,
                    $"程序集声明与当前插件目录相关的托管类型 {fullName}，包含 {declaredMethodCount:N0} 个方法。",
                    artifact.Name,
                    $"type {fullName}",
                    EvidenceConfidence.Confirmed,
                    EvidenceProperties.Create(
                        ("AssemblyId", artifact.ComponentId),
                        ("TypeName", fullName),
                        ("Attributes", typeDefinition.Attributes),
                        ("DeclaredMethodCount", declaredMethodCount))));
                builder.AddEdge(new EvidenceEdge(assemblyNodeId, typeNodeId, "declares-type", EvidenceConfidence.Confirmed));
            }

            if (methodCount > _options.MaxMethods)
            {
                builder.AddWarning(
                    $"{artifact.Name}: contains more than {_options.MaxMethods:N0} methods; only aggregate counts were retained.");
            }

            builder.AddNode(new EvidenceNode(
                assemblyNodeId,
                "PluginAssembly",
                metadataName,
                $"已安全读取程序集元数据：{typeCount:N0} 个类型、{methodCount:N0} 个方法；" +
                $"仅保留 {exposedTypeCount:N0} 个当前插件目录相关类型，不为每个方法建立证据节点。",
                artifact.Name,
                "PE/CLR metadata",
                EvidenceConfidence.Confirmed,
                EvidenceProperties.Create(
                    ("DeclaredTypeCount", typeCount),
                    ("DeclaredMethodCount", methodCount),
                    ("RelevantTypeCount", exposedTypeCount),
                    ("InventoryComplete", inventoryComplete))));
        }
        catch (BadImageFormatException exception)
        {
            builder.AddWarning($"{artifact.Name}: invalid or damaged PE/CLR metadata ({exception.Message}).");
        }
        catch (IOException exception)
        {
            builder.AddWarning($"{artifact.Name}: PE metadata could not be read ({exception.Message}).");
        }
        catch (InvalidOperationException exception)
        {
            builder.AddWarning($"{artifact.Name}: PE metadata inspection stopped ({exception.Message}).");
        }

        return builder.Build();
    }

    private string ReadName(MetadataReader metadata, StringHandle handle)
    {
        var value = metadata.GetString(handle);
        if (value.Length > _options.MaxMetadataNameLength)
        {
            throw new InvalidOperationException(
                $"Metadata name exceeds {_options.MaxMetadataNameLength:N0} characters.");
        }

        if (value.Any(character => char.IsControl(character) && character is not '\t'))
        {
            throw new InvalidOperationException("Metadata name contains control characters.");
        }

        return value;
    }

    private string? GetAssemblyName(MetadataReader metadata)
    {
        if (!metadata.IsAssembly)
        {
            return null;
        }

        var definition = metadata.GetAssemblyDefinition();
        return ReadName(metadata, definition.Name);
    }
}
