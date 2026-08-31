using System.Collections.Concurrent;
using CrmLogicLens.Api.Storage;
using CrmLogicLens.Core;

namespace CrmLogicLens.Api.Services;

public sealed record DecompiledArtifactResult(
    ArtifactUpload? Artifact,
    IReadOnlyList<string> Warnings,
    bool Created);

/// <summary>
/// Produces and caches decompiled source only after a model tool has selected a
/// concrete plug-in step. The assembly is resolved by the server from the current
/// snapshot; callers cannot supply a path, hash, or arbitrary DLL payload.
/// </summary>
public sealed class DecompiledArtifactService(
    IAnalysisStore store,
    DecompilerProcessService decompiler,
    ILogger<DecompiledArtifactService> logger)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<DecompiledArtifactResult> GetOrCreateAsync(
        StoredSnapshot snapshot,
        StoredArtifact assembly,
        CancellationToken cancellationToken)
    {
        if (assembly.Kind != ArtifactKind.PluginAssembly ||
            !snapshot.Artifacts.Any(candidate =>
                candidate.Kind == ArtifactKind.PluginAssembly &&
                string.Equals(candidate.Sha256, assembly.Sha256, StringComparison.OrdinalIgnoreCase)))
        {
            return new DecompiledArtifactResult(null, ["插件程序集不属于当前快照，已拒绝反编译。"], false);
        }

        var derivedName = CreateDerivedName(assembly.Name);
        var existing = await store.LoadArtifactAsync(
            snapshot.SnapshotId,
            ArtifactKind.DecompiledCSharp,
            derivedName,
            cancellationToken);
        if (existing is not null)
        {
            return new DecompiledArtifactResult(existing, [], false);
        }

        var key = $"{snapshot.SnapshotId:D}:{assembly.Sha256}";
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            existing = await store.LoadArtifactAsync(
                snapshot.SnapshotId,
                ArtifactKind.DecompiledCSharp,
                derivedName,
                cancellationToken);
            if (existing is not null)
            {
                return new DecompiledArtifactResult(existing, [], false);
            }

            var assemblyUpload = await store.LoadArtifactAsync(
                snapshot.SnapshotId,
                ArtifactKind.PluginAssembly,
                assembly.Name,
                cancellationToken);
            if (assemblyUpload is null)
            {
                return new DecompiledArtifactResult(null, ["当前快照中的插件 DLL 不可用或完整性校验失败。"], false);
            }

            var enrichment = await decompiler.EnrichAsync(
                new SnapshotUpload(snapshot.Context, [assemblyUpload], snapshot.CapturedAt),
                cancellationToken);
            var source = enrichment.Upload.Artifacts.FirstOrDefault(artifact =>
                artifact.Kind == ArtifactKind.DecompiledCSharp &&
                string.Equals(artifact.Name, derivedName, StringComparison.OrdinalIgnoreCase));
            if (source is null)
            {
                return new DecompiledArtifactResult(null, enrichment.Warnings, false);
            }

            byte[] sourceBytes;
            try
            {
                sourceBytes = Convert.FromBase64String(source.ContentBase64);
            }
            catch (FormatException)
            {
                return new DecompiledArtifactResult(null, ["隔离反编译器返回了无效源码编码。"], false);
            }

            var storedSource = await store.StoreArtifactAsync(new PreparedArtifact(
                source.Kind,
                source.Name,
                source.ComponentId,
                source.Version,
                source.MediaType,
                sourceBytes,
                source.SourceUrl), cancellationToken);
            var latest = await store.GetSnapshotAsync(snapshot.SnapshotId, cancellationToken)
                ?? throw new InvalidDataException("The snapshot disappeared while caching decompiled source.");
            if (!latest.Artifacts.Any(candidate =>
                    candidate.Kind == ArtifactKind.DecompiledCSharp &&
                    string.Equals(candidate.Name, storedSource.Name, StringComparison.OrdinalIgnoreCase)))
            {
                await store.SaveSnapshotAsync(
                    latest with { Artifacts = latest.Artifacts.Append(storedSource).ToArray() },
                    cancellationToken);
            }

            logger.LogInformation(
                "Created on-demand decompiled evidence for snapshot {SnapshotId}, assembly {AssemblyName}",
                snapshot.SnapshotId,
                SafeName(assembly.Name));
            return new DecompiledArtifactResult(source, enrichment.Warnings, true);
        }
        finally
        {
            gate.Release();
            if (gate.CurrentCount == 1)
            {
                _locks.TryRemove(new KeyValuePair<string, SemaphoreSlim>(key, gate));
            }
        }
    }

    private static string CreateDerivedName(string originalName)
    {
        var stem = originalName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? originalName[..^4]
            : originalName;
        return $"{stem[..Math.Min(220, stem.Length)]}.decompiled.cs";
    }

    private static string SafeName(string name)
    {
        var value = name.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value[..Math.Min(160, value.Length)];
    }
}
