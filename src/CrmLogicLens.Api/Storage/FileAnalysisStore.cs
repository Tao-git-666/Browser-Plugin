using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CrmLogicLens.Api.Configuration;
using CrmLogicLens.Core;
using Microsoft.Extensions.Options;

namespace CrmLogicLens.Api.Storage;

public sealed class FileAnalysisStore : IAnalysisStore
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly string _rootDirectory;
    private readonly ILogger<FileAnalysisStore> _logger;

    public FileAnalysisStore(
        IOptions<StorageOptions> options,
        IHostEnvironment environment,
        ILogger<FileAnalysisStore> logger)
    {
        var configuredPath = options.Value.DataDirectory;
        var combinedPath = Path.IsPathFullyQualified(configuredPath)
            ? configuredPath
            : Path.Combine(environment.ContentRootPath, configuredPath);

        _rootDirectory = Path.GetFullPath(combinedPath);
        if (string.Equals(
                _rootDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetPathRoot(_rootDirectory)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Storage:DataDirectory cannot be a filesystem root.");
        }

        _logger = logger;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(GetSafePath("artifacts", "sha256"));
        Directory.CreateDirectory(GetSafePath("snapshots"));
        Directory.CreateDirectory(GetSafePath("jobs"));
        Directory.CreateDirectory(GetSafePath("analyses"));
        return Task.CompletedTask;
    }

    public async Task<StoredArtifact> StoreArtifactAsync(
        PreparedArtifact artifact,
        CancellationToken cancellationToken = default)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(artifact.Content));
        var directory = GetSafePath("artifacts", "sha256", hash[..2]);
        Directory.CreateDirectory(directory);
        var path = GetSafePath("artifacts", "sha256", hash[..2], $"{hash}.blob");

        if (!File.Exists(path))
        {
            await WriteBytesAtomicallyAsync(path, artifact.Content, cancellationToken);
        }

        return new StoredArtifact(
            artifact.Kind,
            artifact.Name,
            artifact.ComponentId,
            artifact.Version,
            artifact.MediaType,
            hash,
            artifact.Content.LongLength,
            artifact.SourceUrl);
    }

    public Task SaveSnapshotAsync(StoredSnapshot snapshot, CancellationToken cancellationToken = default) =>
        WriteJsonAtomicallyAsync(GetEntityPath("snapshots", snapshot.SnapshotId), snapshot, cancellationToken);

    public Task<StoredSnapshot?> GetSnapshotAsync(Guid snapshotId, CancellationToken cancellationToken = default) =>
        ReadJsonAsync<StoredSnapshot>(GetEntityPath("snapshots", snapshotId), cancellationToken);

    public async Task<SnapshotUpload?> LoadSnapshotUploadAsync(
        Guid snapshotId,
        CancellationToken cancellationToken = default)
    {
        var stored = await GetSnapshotAsync(snapshotId, cancellationToken);
        if (stored is null)
        {
            return null;
        }

        var artifacts = new List<ArtifactUpload>(stored.Artifacts.Count);
        foreach (var artifact in stored.Artifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = await ReadArtifactBytesAsync(artifact, cancellationToken);
            artifacts.Add(new ArtifactUpload(
                artifact.Kind,
                artifact.Name,
                artifact.ComponentId,
                artifact.Version,
                artifact.MediaType,
                Convert.ToBase64String(content),
                artifact.SourceUrl));
        }

        return new SnapshotUpload(stored.Context, artifacts, stored.CapturedAt);
    }

    public async Task<ArtifactUpload?> LoadArtifactAsync(
        Guid snapshotId,
        ArtifactKind kind,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var stored = await GetSnapshotAsync(snapshotId, cancellationToken);
        var artifact = stored?.Artifacts.FirstOrDefault(candidate =>
            candidate.Kind == kind &&
            string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
        if (artifact is null)
        {
            return null;
        }

        var content = await ReadArtifactBytesAsync(artifact, cancellationToken);
        return new ArtifactUpload(
            artifact.Kind,
            artifact.Name,
            artifact.ComponentId,
            artifact.Version,
            artifact.MediaType,
            Convert.ToBase64String(content),
            artifact.SourceUrl);
    }

    public Task SaveJobAsync(AnalysisJob job, CancellationToken cancellationToken = default) =>
        WriteJsonAtomicallyAsync(GetEntityPath("jobs", job.JobId), job, cancellationToken);

    public Task<AnalysisJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        ReadJsonAsync<AnalysisJob>(GetEntityPath("jobs", jobId), cancellationToken);

    public async Task<IReadOnlyList<AnalysisJob>> GetRecoverableJobsAsync(
        CancellationToken cancellationToken = default)
    {
        var jobsDirectory = GetSafePath("jobs");
        if (!Directory.Exists(jobsDirectory))
        {
            return [];
        }

        var jobs = new List<AnalysisJob>();
        foreach (var path in Directory.EnumerateFiles(jobsDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var job = await ReadJsonPathAsync<AnalysisJob>(path, cancellationToken);
                if (job is not null &&
                    job.Status is AnalysisJobStatus.Pending or AnalysisJobStatus.Running)
                {
                    jobs.Add(job);
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                _logger.LogError(exception, "Could not read persisted analysis job {JobPath}", path);
            }
        }

        return jobs.OrderBy(job => job.CreatedAt).ToArray();
    }

    public Task SaveAnalysisAsync(AnalysisResult result, CancellationToken cancellationToken = default) =>
        WriteJsonAtomicallyAsync(GetEntityPath("analyses", result.SnapshotId), result, cancellationToken);

    public Task<AnalysisResult?> GetAnalysisAsync(Guid snapshotId, CancellationToken cancellationToken = default) =>
        ReadJsonAsync<AnalysisResult>(GetEntityPath("analyses", snapshotId), cancellationToken);

    public async Task<bool> CheckWritableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await InitializeAsync(cancellationToken);
            var checkPath = GetSafePath($".health-{Guid.NewGuid():N}.tmp");
            await using var stream = new FileStream(
                checkPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                1,
                FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            await stream.WriteAsync(new byte[] { 0x1 }, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "The data directory is not writable");
            return false;
        }
    }

    private async Task<byte[]> ReadArtifactBytesAsync(
        StoredArtifact artifact,
        CancellationToken cancellationToken)
    {
        if (!IsSha256(artifact.Sha256))
        {
            throw new InvalidDataException("A persisted artifact hash is invalid.");
        }

        var path = GetSafePath("artifacts", "sha256", artifact.Sha256[..2], $"{artifact.Sha256}.blob");
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists || fileInfo.Length != artifact.SizeBytes)
        {
            throw new InvalidDataException("A persisted artifact is missing or has an unexpected size.");
        }

        var content = await File.ReadAllBytesAsync(path, cancellationToken);
        var actualHash = Convert.ToHexStringLower(SHA256.HashData(content));
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actualHash),
                Convert.FromHexString(artifact.Sha256)))
        {
            throw new InvalidDataException("A persisted artifact failed its integrity check.");
        }

        return content;
    }

    private async Task<T?> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        return await ReadJsonPathAsync<T>(path, cancellationToken);
    }

    private static async Task<T?> ReadJsonPathAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
    }

    private static async Task WriteJsonAtomicallyAsync<T>(
        string destinationPath,
        T value,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("The destination directory is invalid.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, destinationPath, true);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private static async Task WriteBytesAtomicallyAsync(
        string destinationPath,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("The destination directory is invalid.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, destinationPath, true);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private string GetEntityPath(string directory, Guid id) =>
        GetSafePath(directory, $"{id:D}.json");

    private string GetSafePath(params string[] segments)
    {
        var path = Path.GetFullPath(Path.Combine([_rootDirectory, .. segments]));
        var rootWithSeparator = _rootDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? _rootDirectory
            : _rootDirectory + Path.DirectorySeparatorChar;

        if (!path.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(path, _rootDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("A storage path escaped the configured data directory.");
        }

        return path;
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Cleanup failure must not hide the original persistence result.
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = false
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}
