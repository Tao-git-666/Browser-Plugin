using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CrmLogicLens.Api.Configuration;
using CrmLogicLens.Core;
using Microsoft.Extensions.Options;

namespace CrmLogicLens.Api.Services;

public sealed record DecompilerEnrichment(
    SnapshotUpload Upload,
    IReadOnlyList<string> Warnings);

public sealed class DecompilerProcessService
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions WorkerJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly DecompilerOptions _options;
    private readonly string? _workerPath;
    private readonly string _temporaryRoot;
    private readonly ILogger<DecompilerProcessService> _logger;

    public DecompilerProcessService(
        IOptions<DecompilerOptions> options,
        IOptions<StorageOptions> storageOptions,
        IHostEnvironment environment,
        ILogger<DecompilerProcessService> logger)
    {
        _options = options.Value;
        _logger = logger;
        _workerPath = ResolveOptionalPath(_options.WorkerPath, environment.ContentRootPath);

        var dataRoot = ResolvePath(storageOptions.Value.DataDirectory, environment.ContentRootPath);
        _temporaryRoot = Path.IsPathFullyQualified(_options.TempDirectory)
            ? Path.GetFullPath(_options.TempDirectory)
            : ResolveRelativeTemporaryPath(dataRoot, _options.TempDirectory);

        RejectFilesystemRoot(_temporaryRoot, "Decompiler:TempDirectory");
        if (string.Equals(
                _temporaryRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                dataRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Decompiler:TempDirectory must be a dedicated subdirectory or absolute directory.");
        }
    }

    public bool IsConfigured => _workerPath is not null;

    public bool IsAvailable => _workerPath is not null && File.Exists(_workerPath);

    public bool EagerAnalysisEnabled => _options.EagerAnalysis;

    public async Task<DecompilerEnrichment> EnrichAsync(
        SnapshotUpload upload,
        CancellationToken cancellationToken)
    {
        var existingSources = upload.Artifacts
            .Where(artifact => artifact.Kind == ArtifactKind.DecompiledCSharp)
            .ToArray();
        var assemblies = upload.Artifacts
            .Where(artifact => artifact.Kind == ArtifactKind.PluginAssembly)
            .Where(assembly => !existingSources.Any(source =>
                (!string.IsNullOrWhiteSpace(assembly.ComponentId) &&
                 string.Equals(source.ComponentId, assembly.ComponentId, StringComparison.OrdinalIgnoreCase)) ||
                string.Equals(source.Name, CreateDerivedName(assembly.Name), StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (assemblies.Length == 0)
        {
            return new DecompilerEnrichment(upload, []);
        }

        if (_workerPath is null)
        {
            return new DecompilerEnrichment(upload,
            [
                $"{assemblies.Length} plug-in assembly artifact(s) were not decompiled because Decompiler:WorkerPath is not configured."
            ]);
        }

        if (!File.Exists(_workerPath))
        {
            return new DecompilerEnrichment(upload,
            [
                $"{assemblies.Length} plug-in assembly artifact(s) were not decompiled because the configured decompiler worker is unavailable."
            ]);
        }

        Directory.CreateDirectory(_temporaryRoot);
        var artifacts = new List<ArtifactUpload>(upload.Artifacts);
        var warnings = new List<string>();
        var count = Math.Min(assemblies.Length, _options.MaxAssembliesPerSnapshot);

        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var assembly = assemblies[index];
            var result = await TryDecompileAsync(assembly, cancellationToken);
            if (result.Artifact is not null)
            {
                artifacts.Add(result.Artifact);
            }

            warnings.AddRange(result.Warnings);
        }

        if (assemblies.Length > count)
        {
            warnings.Add(
                $"{assemblies.Length - count} plug-in assembly artifact(s) were not decompiled because the per-snapshot limit is {_options.MaxAssembliesPerSnapshot}.");
        }

        return new DecompilerEnrichment(upload with { Artifacts = artifacts }, warnings);
    }

    private async Task<SingleDecompilationResult> TryDecompileAsync(
        ArtifactUpload assembly,
        CancellationToken cancellationToken)
    {
        var displayName = SafeDisplayName(assembly.Name);
        byte[] input;
        try
        {
            input = Convert.FromBase64String(assembly.ContentBase64);
        }
        catch (FormatException)
        {
            return Failed(displayName, "the persisted assembly payload is invalid");
        }

        if (input.LongLength > _options.MaxInputBytes)
        {
            return Failed(displayName, $"it exceeds the {_options.MaxInputBytes}-byte decompiler input limit");
        }

        var invocationId = Guid.NewGuid().ToString("N");
        var invocationDirectory = GetSafeInvocationDirectory(invocationId);
        var inputPath = Path.Combine(invocationDirectory, "input.dll");
        var outputPath = Path.Combine(invocationDirectory, "output.cs");

        try
        {
            Directory.CreateDirectory(invocationDirectory);
            await WriteInputAsync(inputPath, input, cancellationToken);
            var expectedHash = Convert.ToHexStringLower(SHA256.HashData(input));
            var processResult = await RunWorkerAsync(inputPath, outputPath, invocationDirectory, cancellationToken);

            if (processResult.TimedOut)
            {
                return Failed(displayName, $"the worker exceeded the {_options.TimeoutSeconds}-second timeout");
            }

            if (processResult.OutputLimitExceeded)
            {
                return Failed(displayName, $"the generated source exceeded the {_options.MaxOutputBytes}-byte output limit");
            }

            if (processResult.ExitCode != 0)
            {
                var diagnostic = ParseWorkerError(processResult.StandardError, invocationDirectory);
                return Failed(displayName, $"the worker returned {diagnostic}");
            }

            WorkerResult? manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<WorkerResult>(processResult.StandardOutput, WorkerJsonOptions);
            }
            catch (JsonException)
            {
                return Failed(displayName, "the worker returned an invalid result manifest");
            }

            if (manifest is null ||
                !string.Equals(manifest.Sha256, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                return Failed(displayName, "the worker result failed its input integrity check");
            }

            var outputInfo = new FileInfo(outputPath);
            if (!outputInfo.Exists || outputInfo.Length is <= 0 || outputInfo.Length > _options.MaxOutputBytes)
            {
                return Failed(
                    displayName,
                    $"the generated source is missing, empty, or exceeds the {_options.MaxOutputBytes}-byte output limit");
            }

            var sourceBytes = await File.ReadAllBytesAsync(outputPath, cancellationToken);
            var source = StrictUtf8.GetString(sourceBytes);
            if (source.IndexOf('\0') >= 0)
            {
                return Failed(displayName, "the generated source contains invalid text");
            }

            var derivedArtifact = new ArtifactUpload(
                ArtifactKind.DecompiledCSharp,
                CreateDerivedName(assembly.Name),
                assembly.ComponentId,
                assembly.Version,
                "text/x-csharp; charset=utf-8",
                Convert.ToBase64String(sourceBytes),
                assembly.SourceUrl);

            var warnings = (manifest.Warnings ?? [])
                .Select(warning => SanitizeDiagnostic(warning, invocationDirectory))
                .Where(warning => !string.IsNullOrWhiteSpace(warning))
                .Select(warning => $"Decompiler warning for '{displayName}': {warning}")
                .ToArray();
            return new SingleDecompilationResult(derivedArtifact, warnings);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or DecoderFallbackException)
        {
            _logger.LogWarning(
                exception,
                "Decompiler invocation {InvocationId} failed for assembly {AssemblyName}",
                invocationId,
                displayName);
            return Failed(displayName, "the isolated worker could not complete; consult the server logs");
        }
        finally
        {
            TryDeleteInvocationDirectory(invocationDirectory, invocationId);
        }
    }

    private async Task<ProcessResult> RunWorkerAsync(
        string inputPath,
        string outputPath,
        string invocationDirectory,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _workerPath!,
            WorkingDirectory = invocationDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("--input");
        startInfo.ArgumentList.Add(inputPath);
        startInfo.ArgumentList.Add("--output");
        startInfo.ArgumentList.Add(outputPath);
        SetMinimalEnvironment(startInfo, invocationDirectory);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            throw new Win32Exception("The decompiler worker could not be started.");
        }

        process.StandardInput.Close();
        var standardOutputTask = ReadBoundedAsync(process.StandardOutput, 16_384);
        var standardErrorTask = ReadBoundedAsync(process.StandardError, _options.MaxDiagnosticCharacters);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

        try
        {
            var exitTask = process.WaitForExitAsync(timeout.Token);
            while (!exitTask.IsCompleted)
            {
                await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token));
                if (File.Exists(outputPath) && new FileInfo(outputPath).Length > _options.MaxOutputBytes)
                {
                    TryKillProcessTree(process);
                    await process.WaitForExitAsync(CancellationToken.None);
                    _ = await standardOutputTask;
                    _ = await standardErrorTask;
                    return new ProcessResult(
                        -1,
                        string.Empty,
                        string.Empty,
                        TimedOut: false,
                        OutputLimitExceeded: true);
                }
            }

            await exitTask;
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            await process.WaitForExitAsync(CancellationToken.None);
            _ = await standardOutputTask;
            _ = await standardErrorTask;

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new ProcessResult(
                -1,
                string.Empty,
                string.Empty,
                TimedOut: true,
                OutputLimitExceeded: false);
        }

        return new ProcessResult(
            process.ExitCode,
            await standardOutputTask,
            await standardErrorTask,
            TimedOut: false,
            OutputLimitExceeded: false);
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int retainedCharacterLimit)
    {
        var retained = new StringBuilder(Math.Min(retainedCharacterLimit, 4_096));
        var buffer = new char[1_024];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None)) > 0)
        {
            var remaining = retainedCharacterLimit - retained.Length;
            if (remaining > 0)
            {
                retained.Append(buffer, 0, Math.Min(read, remaining));
            }
        }

        return retained.ToString();
    }

    private static async Task WriteInputAsync(
        string path,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(content, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private string ParseWorkerError(string standardError, string invocationDirectory)
    {
        try
        {
            var error = JsonSerializer.Deserialize<WorkerError>(standardError, WorkerJsonOptions);
            if (error is not null && !string.IsNullOrWhiteSpace(error.Code))
            {
                var message = SanitizeDiagnostic(error.Message, invocationDirectory);
                return string.IsNullOrWhiteSpace(message)
                    ? $"error '{error.Code}'"
                    : $"error '{error.Code}': {message}";
            }
        }
        catch (JsonException)
        {
        }

        var diagnostic = SanitizeDiagnostic(standardError, invocationDirectory);
        return string.IsNullOrWhiteSpace(diagnostic)
            ? "an unspecified error"
            : $"an error: {diagnostic}";
    }

    private string SanitizeDiagnostic(string? diagnostic, string invocationDirectory)
    {
        if (string.IsNullOrWhiteSpace(diagnostic))
        {
            return string.Empty;
        }

        var sanitized = diagnostic
            .Replace(invocationDirectory, "<temp>", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(_workerPath))
        {
            sanitized = sanitized.Replace(_workerPath, "<worker>", StringComparison.OrdinalIgnoreCase);
        }

        sanitized = sanitized
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        return sanitized.Length <= _options.MaxDiagnosticCharacters
            ? sanitized
            : sanitized[.._options.MaxDiagnosticCharacters];
    }

    private static void SetMinimalEnvironment(ProcessStartInfo startInfo, string invocationDirectory)
    {
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        var windowsDirectory = Environment.GetEnvironmentVariable("WINDIR");
        startInfo.Environment.Clear();
        if (!string.IsNullOrWhiteSpace(systemRoot))
        {
            startInfo.Environment["SystemRoot"] = systemRoot;
            startInfo.Environment["PATH"] = Path.Combine(systemRoot, "System32");
        }

        if (!string.IsNullOrWhiteSpace(windowsDirectory))
        {
            startInfo.Environment["WINDIR"] = windowsDirectory;
        }

        startInfo.Environment["TEMP"] = invocationDirectory;
        startInfo.Environment["TMP"] = invocationDirectory;
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["COMPlus_EnableDiagnostics"] = "0";
    }

    private string GetSafeInvocationDirectory(string invocationId)
    {
        if (invocationId.Length != 32 || !invocationId.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException("The decompiler invocation identifier is invalid.");
        }

        var candidate = Path.GetFullPath(Path.Combine(_temporaryRoot, invocationId));
        var rootWithSeparator = _temporaryRoot.EndsWith(Path.DirectorySeparatorChar)
            ? _temporaryRoot
            : _temporaryRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("A decompiler path escaped the configured temporary directory.");
        }

        return candidate;
    }

    private void TryDeleteInvocationDirectory(string directory, string invocationId)
    {
        try
        {
            var expected = GetSafeInvocationDirectory(invocationId);
            if (string.Equals(directory, expected, StringComparison.OrdinalIgnoreCase) && Directory.Exists(expected))
            {
                Directory.Delete(expected, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Could not clean decompiler invocation directory {InvocationId}", invocationId);
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
        }
    }

    private static string ResolvePath(string configuredPath, string basePath) =>
        Path.GetFullPath(Path.IsPathFullyQualified(configuredPath)
            ? configuredPath
            : Path.Combine(basePath, configuredPath));

    private static string? ResolveOptionalPath(string? configuredPath, string basePath) =>
        string.IsNullOrWhiteSpace(configuredPath) ? null : ResolvePath(configuredPath, basePath);

    private static string ResolveRelativeTemporaryPath(string dataRoot, string configuredPath)
    {
        var candidate = Path.GetFullPath(Path.Combine(dataRoot, configuredPath));
        var rootWithSeparator = dataRoot.EndsWith(Path.DirectorySeparatorChar)
            ? dataRoot
            : dataRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "A relative Decompiler:TempDirectory must remain inside Storage:DataDirectory.");
        }

        return candidate;
    }

    private static void RejectFilesystemRoot(string path, string settingName)
    {
        if (string.Equals(
                path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetPathRoot(path)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{settingName} cannot be a filesystem root.");
        }
    }

    private static string SafeDisplayName(string name)
    {
        var value = name.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= 160 ? value : value[..160];
    }

    private static string CreateDerivedName(string originalName)
    {
        var stem = originalName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? originalName[..^4]
            : originalName;
        if (stem.Length > 220)
        {
            stem = stem[..220];
        }

        return $"{stem}.decompiled.cs";
    }

    private static SingleDecompilationResult Failed(string assemblyName, string reason) =>
        new(null, [$"Plug-in assembly '{assemblyName}' was not decompiled because {reason.Trim().TrimEnd('.')}."]);

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError,
        bool TimedOut,
        bool OutputLimitExceeded);

    private sealed record SingleDecompilationResult(
        ArtifactUpload? Artifact,
        IReadOnlyList<string> Warnings);

    private sealed record WorkerError(string Code, string Message);

    private sealed record WorkerResult(
        string AssemblyName,
        string AssemblyVersion,
        string Sha256,
        int TypeCount,
        int MethodCount,
        int SourceCharacters,
        string OutputPath,
        IReadOnlyList<string>? Warnings);
}
