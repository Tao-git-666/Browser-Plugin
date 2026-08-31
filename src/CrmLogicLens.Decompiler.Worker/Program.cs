using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.ProjectDecompiler;
using ICSharpCode.Decompiler.Metadata;

return await WorkerProgram.RunAsync(args);

internal static class WorkerProgram
{
    private const long MaximumAssemblyBytes = 64L * 1024 * 1024;

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = ParseArguments(args);
            if (options is null)
            {
                await Console.Error.WriteLineAsync(
                    "Usage: CrmLogicLens.Decompiler.Worker --input <assembly.dll> --output <source.cs>");
                return 2;
            }

            var inputPath = Path.GetFullPath(options.InputPath);
            var outputPath = Path.GetFullPath(options.OutputPath);
            var inputFile = new FileInfo(inputPath);

            if (!inputFile.Exists || !inputFile.Extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Input must be an existing .dll file.");
            }

            if (inputFile.Length is <= 0 or > MaximumAssemblyBytes)
            {
                throw new InvalidDataException($"Assembly size must be between 1 byte and {MaximumAssemblyBytes} bytes.");
            }

            var metadata = ReadMetadata(inputPath);
            var hash = await ComputeSha256Async(inputPath);

            var settings = new DecompilerSettings
            {
                UseDebugSymbols = false,
                ShowXmlDocumentation = false
            };

            // Use a non-throwing resolver so a D365 plug-in can still be decompiled when
            // Microsoft.Xrm.Sdk or another referenced assembly is not installed beside it.
            // Resolver inputs are metadata-only; no referenced assembly is executed.
            using var module = new PEFile(inputPath);
            var targetFramework = TargetServices.DetectTargetFramework(module);
            var resolver = new UniversalAssemblyResolver(
                inputPath,
                throwOnError: false,
                targetFramework: targetFramework.Moniker);
            resolver.AddSearchDirectory(Path.GetDirectoryName(inputPath)!);
            resolver.AddSearchDirectory(AppContext.BaseDirectory);

            var decompiler = new CSharpDecompiler(module, resolver, settings);
            var syntaxTree = decompiler.DecompileWholeModuleAsSingleFile();
            var source = syntaxTree.ToString();

            var outputDirectory = Path.GetDirectoryName(outputPath)
                ?? throw new InvalidDataException("Output path must include a directory.");
            Directory.CreateDirectory(outputDirectory);

            var temporaryPath = outputPath + ".tmp-" + Guid.NewGuid().ToString("N");
            await File.WriteAllTextAsync(temporaryPath, source, new UTF8Encoding(false));
            File.Move(temporaryPath, outputPath, overwrite: true);

            var result = new WorkerResult(
                metadata.AssemblyName,
                metadata.AssemblyVersion,
                hash,
                metadata.TypeCount,
                metadata.MethodCount,
                source.Length,
                outputPath,
                Array.Empty<string>());

            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(result, JsonOptions));
            return 0;
        }
        catch (BadImageFormatException exception)
        {
            await WriteErrorAsync("not_managed_assembly", exception.Message);
            return 3;
        }
        catch (Exception exception)
        {
            await WriteErrorAsync("decompilation_failed", exception.Message);
            return 1;
        }
    }

    private static WorkerOptions? ParseArguments(IReadOnlyList<string> args)
    {
        string? input = null;
        string? output = null;

        for (var index = 0; index < args.Count; index++)
        {
            if (args[index].Equals("--input", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Count)
            {
                input = args[++index];
            }
            else if (args[index].Equals("--output", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Count)
            {
                output = args[++index];
            }
            else
            {
                return null;
            }
        }

        return string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(output)
            ? null
            : new WorkerOptions(input, output);
    }

    private static AssemblyMetadata ReadMetadata(string path)
    {
        using var stream = File.OpenRead(path);
        using var peReader = new PEReader(stream, PEStreamOptions.PrefetchMetadata);
        if (!peReader.HasMetadata)
        {
            throw new BadImageFormatException("The PE file does not contain .NET metadata.");
        }

        var reader = peReader.GetMetadataReader();
        if (!reader.IsAssembly)
        {
            throw new BadImageFormatException("The metadata does not describe a managed assembly.");
        }

        var definition = reader.GetAssemblyDefinition();
        return new AssemblyMetadata(
            reader.GetString(definition.Name),
            definition.Version.ToString(),
            reader.TypeDefinitions.Count,
            reader.MethodDefinitions.Count);
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexStringLower(hash);
    }

    private static Task WriteErrorAsync(string code, string message)
    {
        var payload = JsonSerializer.Serialize(new WorkerError(code, message), JsonOptions);
        return Console.Error.WriteLineAsync(payload);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private sealed record WorkerOptions(string InputPath, string OutputPath);
    private sealed record AssemblyMetadata(string AssemblyName, string AssemblyVersion, int TypeCount, int MethodCount);
    private sealed record WorkerError(string Code, string Message);
    private sealed record WorkerResult(
        string AssemblyName,
        string AssemblyVersion,
        string Sha256,
        int TypeCount,
        int MethodCount,
        int SourceCharacters,
        string OutputPath,
        IReadOnlyList<string> Warnings);
}
