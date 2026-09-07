using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CrmLogicLens.Api.Configuration;
using CrmLogicLens.Api.Errors;
using CrmLogicLens.Api.Storage;
using CrmLogicLens.Api.Validation;
using CrmLogicLens.Core;
using Microsoft.Extensions.Options;

namespace CrmLogicLens.Api.Services;

public sealed record CodeLibraryBatch(Guid? LibraryId, SnapshotUpload Upload, int ExpectedAssemblies, bool DiscoveryComplete);
public sealed record CodeLibraryManifest(string Environment, int ExpectedAssemblies, bool DiscoveryComplete, DateTimeOffset CreatedAt);
public sealed record CodeLibraryEntry(string AssemblyId, string AssemblyName, string Hash, Guid SnapshotId,
    string SourceName, DateTimeOffset IndexedAt, string[] Terms, bool TermsComplete, EvidenceNode[] Steps);
public sealed record CodeLibraryHit(CodeLibraryEntry Entry, string Excerpt, int Line, bool HasMore, int? NextContextOffset);

/// <summary>Server-owned indexes of explicitly synchronized code. Library handles are scoped
/// to an organization, and source bytes always come from the validated artifact store.</summary>
public sealed class EnvironmentCodeLibrary(
    IAnalysisStore store, DecompiledArtifactService decompiled, SnapshotUploadValidator validator,
    IOptions<StorageOptions> options, IHostEnvironment environment)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _importGate = new(1, 1);
    private string Root => Path.Combine(Path.GetFullPath(Path.IsPathRooted(options.Value.DataDirectory)
        ? options.Value.DataDirectory : Path.Combine(environment.ContentRootPath, options.Value.DataDirectory)), "code-library-v1");

    public static string EnvironmentKey(CrmPageContext context) => Hash(context.OrganizationUrl.TrimEnd('/').ToLowerInvariant());
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private string LibraryPath(Guid id) => Path.Combine(Root, id.ToString("N"));

    public async Task<object> ImportAsync(CodeLibraryBatch batch, CancellationToken cancellationToken)
    {
        if (batch is null) throw new ApiInputException("缺少代码库同步批次。", "body");
        if (batch.ExpectedAssemblies is < 1 or > 300 || batch.LibraryId == Guid.Empty)
            throw new ApiInputException("代码库批次参数无效。", "codeLibrary");
        var prepared = validator.ValidateAndPrepare(batch.Upload, DateTimeOffset.UtcNow);
        if (prepared.Count != 2 || prepared.Count(a => a.Kind == ArtifactKind.PluginAssembly) != 1 ||
            prepared.Count(a => a.Kind == ArtifactKind.PluginCatalog) != 1)
            throw new ApiInputException("每批必须包含一个插件 DLL 和它的注册目录。", "artifacts");
        var assemblyInput = batch.Upload.Artifacts.Single(a => a.Kind == ArtifactKind.PluginAssembly);
        if (!Guid.TryParse(assemblyInput.ComponentId, out var assemblyId))
            throw new ApiInputException("程序集 ID 无效。", "componentId");
        await _importGate.WaitAsync(cancellationToken);
        try
        {
            var libraryId = batch.LibraryId ?? Guid.NewGuid();
            var directory = LibraryPath(libraryId);
            if (batch.LibraryId is not null)
            {
                var manifest = await RequireManifestAsync(libraryId, batch.Upload.Context, cancellationToken);
                if (manifest.ExpectedAssemblies != batch.ExpectedAssemblies)
                    throw new ApiInputException("同步计划已变化，请重新同步。", "expectedAssemblies");
                if (!batch.DiscoveryComplete && manifest.DiscoveryComplete)
                    await WriteAsync(Path.Combine(directory, "manifest.json"), manifest with { DiscoveryComplete = false }, cancellationToken);
            }
            else
            {
                Directory.CreateDirectory(directory);
                await WriteAsync(Path.Combine(directory, "manifest.json"), new CodeLibraryManifest(
                    EnvironmentKey(batch.Upload.Context), batch.ExpectedAssemblies, batch.DiscoveryComplete, DateTimeOffset.UtcNow), cancellationToken);
            }
            var artifacts = new List<StoredArtifact>();
            foreach (var artifact in prepared) artifacts.Add(await store.StoreArtifactAsync(artifact, cancellationToken));
            var snapshot = new StoredSnapshot(Guid.NewGuid(), Guid.Empty, batch.Upload.Context, artifacts,
                batch.Upload.CapturedAt, DateTimeOffset.UtcNow);
            await store.SaveSnapshotAsync(snapshot, cancellationToken);
            var assembly = artifacts.Single(a => a.Kind == ArtifactKind.PluginAssembly);
            var cacheDirectory = Path.Combine(Root, "cache", EnvironmentKey(snapshot.Context));
            Directory.CreateDirectory(cacheDirectory);
            var cachePath = Path.Combine(cacheDirectory, $"{assembly.Sha256}.json");
            ArtifactUpload? source = null;
            if (File.Exists(cachePath))
            {
                try
                {
                    var prior = await ReadAsync<CodeLibraryEntry>(cachePath, cancellationToken);
                    if (prior?.Hash == assembly.Sha256)
                        source = await store.LoadArtifactAsync(prior.SnapshotId, ArtifactKind.DecompiledCSharp, prior.SourceName, cancellationToken);
                }
                catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
                {
                    source = null; // Cache is derivable; re-decompile if the old artifact is unavailable.
                }
            }
            var cacheHit = source is not null;
            IReadOnlyList<string> warnings = [];
            if (source is null)
            {
                var result = await decompiled.GetOrCreateAsync(snapshot, assembly, cancellationToken);
                source = result.Artifact;
                warnings = result.Warnings;
            }
            if (source is null) throw new ApiInputException("插件未能反编译；该程序集尚未加入代码库。", "assembly", 422);
            var sourceBytes = Convert.FromBase64String(source.ContentBase64);
            var storedSource = await store.StoreArtifactAsync(new PreparedArtifact(ArtifactKind.DecompiledCSharp,
                source.Name, assembly.ComponentId, assembly.Version, source.MediaType, sourceBytes, source.SourceUrl), cancellationToken);
            await store.SaveSnapshotAsync(snapshot with { Artifacts = artifacts.Append(storedSource).ToArray() }, cancellationToken);
            var catalog = batch.Upload.Artifacts.Single(a => a.Kind == ArtifactKind.PluginCatalog);
            var graph = new PluginCatalogAnalyzer().Analyze(new ArtifactDecoder().Decode(catalog));
            if (graph.Warnings.Count > 0 || warnings.Count > 0)
            {
                var manifest = await RequireManifestAsync(libraryId, snapshot.Context, cancellationToken);
                await WriteAsync(Path.Combine(directory, "manifest.json"), manifest with { DiscoveryComplete = false }, cancellationToken);
                warnings = warnings.Concat(graph.Warnings).ToArray();
            }
            var text = Encoding.UTF8.GetString(sourceBytes);
            var terms = Regex.Matches(text, @"[A-Za-z_][A-Za-z0-9_.]{2,127}", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)
                .Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).Take(20_001).ToArray();
            var entry = new CodeLibraryEntry(assemblyId.ToString("D"), assembly.Name, assembly.Sha256,
                snapshot.SnapshotId, source.Name, DateTimeOffset.UtcNow, terms.Take(20_000).ToArray(), terms.Length <= 20_000,
                graph.Nodes.Where(n => n.Kind == "PluginStep" && Guid.TryParse(n.Properties?.GetValueOrDefault("AssemblyId"), out var id) && id == assemblyId).ToArray());
            await WriteAsync(Path.Combine(directory, $"{assemblyId:N}.entry.json"), entry, cancellationToken);
            await WriteAsync(cachePath, entry, cancellationToken);
            return new { libraryId, assembly = assembly.Name, cacheHit, indexed = Directory.GetFiles(directory, "*.entry.json").Length, warnings };
        }
        finally { _importGate.Release(); }
    }

    public async Task<(CodeLibraryManifest Manifest, IReadOnlyList<CodeLibraryEntry> Entries)> EntriesAsync(
        Guid libraryId, CrmPageContext context, CancellationToken cancellationToken)
    {
        var manifest = await RequireManifestAsync(libraryId, context, cancellationToken);
        var entries = new List<CodeLibraryEntry>();
        foreach (var path in Directory.EnumerateFiles(LibraryPath(libraryId), "*.entry.json").Take(300))
        {
            var entry = await ReadAsync<CodeLibraryEntry>(path, cancellationToken);
            if (entry is not null) entries.Add(entry);
        }
        return (manifest, entries);
    }

    public async Task<CodeLibraryHit?> ReadHitAsync(CodeLibraryEntry entry, string keyword, CancellationToken cancellationToken,
        int occurrence = 0, int contextOffset = 0, int pageSize = 4000)
    {
        var source = await store.LoadArtifactAsync(entry.SnapshotId, ArtifactKind.DecompiledCSharp, entry.SourceName, cancellationToken);
        if (source is null) return null;
        var text = Encoding.UTF8.GetString(Convert.FromBase64String(source.ContentBase64));
        var index = text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
        for (var skip = 0; skip < occurrence && index >= 0; skip++)
            index = text.IndexOf(keyword, index + keyword.Length, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;
        var start = Math.Max(0, index - 1600) + contextOffset;
        if (start >= text.Length) return null;
        var end = Math.Min(text.Length, start + pageSize);
        var line = text.AsSpan(0, start).Count('\n') + 1;
        // Redact suspect credential-bearing lines before they can enter model context.
        var excerpt = string.Join('\n', text[start..end].Split('\n').Select(value =>
            Regex.IsMatch(value, @"(?i)(password|clientsecret|api[_-]?key|connectionstring|bearer\s)", RegexOptions.CultureInvariant)
                ? "[已隐藏疑似凭据的代码行]" : value));
        return new CodeLibraryHit(entry, excerpt, line,
            text.IndexOf(keyword, index + keyword.Length, StringComparison.OrdinalIgnoreCase) >= 0,
            end < text.Length ? contextOffset + pageSize : null);
    }

    private async Task<CodeLibraryManifest> RequireManifestAsync(Guid id, CrmPageContext context, CancellationToken cancellationToken)
    {
        var path = Path.Combine(LibraryPath(id), "manifest.json");
        var manifest = File.Exists(path) ? await ReadAsync<CodeLibraryManifest>(path, cancellationToken) : null;
        if (manifest is null || manifest.Environment != EnvironmentKey(context))
            throw new ApiInputException("代码库不存在或不属于当前 CRM 环境。请重新同步。", "libraryId", 404);
        return manifest;
    }

    private static async Task<T?> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
    }

    private static async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, JsonOptions), cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
