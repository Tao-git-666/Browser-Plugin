using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CrmLogicLens.Api.Configuration;
using CrmLogicLens.Api.Errors;
using CrmLogicLens.Api.Services;
using CrmLogicLens.Api.Storage;
using CrmLogicLens.Api.Validation;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CrmLogicLens.Core.Tests;

public sealed class EnvironmentCodeLibraryTests
{
    [Fact]
    public async Task Import_ReusesHashMatchedCodeAndRefreshesRegistrationAcrossSnapshots()
    {
        using var fixture = new Fixture();
        await fixture.SeedCacheAsync();
        var first = await fixture.ImportAsync("statecode");
        var second = await fixture.ImportAsync("new_status");
        Assert.NotEqual(first, second);
        var (_, entries) = await fixture.Library.EntriesAsync(second, fixture.Context, default);
        var entry = Assert.Single(entries);
        Assert.Equal("new_status", Assert.Single(entry.Steps).Properties!["FilteringAttributes"]);
        Assert.Equal("new_workorder", entry.Steps[0].Properties!["Entity"]);
        var hit = await fixture.Library.ReadHitAsync(entry, "new_equipment", default);
        Assert.NotNull(hit);
        Assert.Contains("service.Create", hit.Excerpt);
        Assert.DoesNotContain("secret-value", hit.Excerpt);
    }

    [Fact]
    public async Task ReadCode_ContinuesLongMethodWithoutSkippingTheWrite()
    {
        using var fixture = new Fixture();
        await fixture.SeedCacheAsync("class Business { void WriteEquipment() {\n" + new string(' ', 6000) +
            "\nservice.Create(new Entity(\"new_equipment\"));\n} }");
        var id = await fixture.ImportAsync("new_status");
        var (_, entries) = await fixture.Library.EntriesAsync(id, fixture.Context, default);
        var entry = Assert.Single(entries);
        var first = await fixture.Library.ReadHitAsync(entry, "WriteEquipment", default);
        Assert.NotNull(first);
        Assert.DoesNotContain("service.Create", first.Excerpt);
        Assert.NotNull(first.NextContextOffset);
        var second = await fixture.Library.ReadHitAsync(entry, "WriteEquipment", default,
            contextOffset: first.NextContextOffset.Value);
        Assert.NotNull(second);
        Assert.Contains("service.Create", second.Excerpt);
        Assert.Null(second.NextContextOffset);
    }

    [Fact]
    public async Task Library_RejectsAnotherOrganizationAndDoesNotReuseChangedDll()
    {
        using var fixture = new Fixture();
        await fixture.SeedCacheAsync();
        var id = await fixture.ImportAsync("statecode");
        await Assert.ThrowsAsync<ApiInputException>(() => fixture.Library.EntriesAsync(id,
            fixture.Context with { OrganizationUrl = "https://crm.example/OtherOrg" }, default));
        // A changed hash must not hit the seeded cache. No worker is configured in this test.
        var upload = fixture.Upload("statecode");
        var changed = upload.Artifacts.Select(a => a.Kind == ArtifactKind.PluginAssembly
            ? a with { ContentBase64 = Convert.ToBase64String(File.ReadAllBytes(typeof(EnvironmentCodeLibrary).Assembly.Location)) } : a).ToArray();
        await Assert.ThrowsAsync<ApiInputException>(() => fixture.Library.ImportAsync(
            new CodeLibraryBatch(null, upload with { Artifacts = changed }, 1, true), default));
    }

    [Fact]
    public async Task Tool_SearchesOtherEntityWriterOnlyWithLibraryHandleAndPreservesCoverage()
    {
        using var fixture = new Fixture();
        await fixture.SeedCacheAsync();
        var id = await fixture.ImportAsync("statecode", expected: 2);
        var snapshot = new StoredSnapshot(Guid.NewGuid(), Guid.NewGuid(), fixture.Context, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var noLibrary = new EvidenceToolSession(fixture.Store, snapshot, new EvidenceGraph([], [], []), codeLibrary: fixture.Library);
        var denied = await noLibrary.ExecuteAsync("search_environment_code", """{"keyword":"new_equipment"}""", default);
        Assert.Contains("未知工具", denied);
        var session = new EvidenceToolSession(fixture.Store, snapshot, new EvidenceGraph([], [], []),
            codeLibrary: fixture.Library, environmentLibraryId: id);
        var json = await session.ExecuteAsync("search_environment_code", """{"keyword":"new_equipment"}""", default);
        using var result = JsonDocument.Parse(json);
        Assert.True(result.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(result.RootElement.GetProperty("complete").GetBoolean());
        var match = Assert.Single(result.RootElement.GetProperty("matches").EnumerateArray());
        Assert.Contains("service.Create", match.GetProperty("excerpt").GetString());
        Assert.Equal("new_workorder", match.GetProperty("candidateSteps")[0].GetProperty("properties").GetProperty("Entity").GetString());
        Assert.Single(session.ExposedCitations);
        Assert.Equal(EvidenceConfidence.Inferred, session.ExposedCitations[0].Confidence);
        var read = await session.ExecuteAsync("read_environment_code",
            JsonSerializer.Serialize(new { keyword = "WriteEquipment", assembly_id = fixture.AssemblyId }), default);
        Assert.Contains("service.Create", read);
        Assert.Contains("read_environment_code", JsonSerializer.Serialize(session.BuildFinalAnswerContext("设备档案由谁创建", 12000)));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "logic-lens-library-tests-" + Guid.NewGuid().ToString("N"));
        public readonly string AssemblyId = Guid.NewGuid().ToString("D");
        public readonly CrmPageContext Context = new("https://crm.example/Org", null, "9.1", "v9.0", "entityrecord", "new_equipment", null, null, null, "设备档案");
        public FileAnalysisStore Store { get; }
        public EnvironmentCodeLibrary Library { get; }
        private readonly byte[] _dll = File.ReadAllBytes(typeof(EvidenceNode).Assembly.Location);
        public Fixture()
        {
            var environment = new TestEnvironment { ContentRootPath = _root };
            var options = Options.Create(new StorageOptions { DataDirectory = "data" });
            Store = new FileAnalysisStore(options, environment, NullLogger<FileAnalysisStore>.Instance);
            var worker = new DecompilerProcessService(Options.Create(new DecompilerOptions()), options, environment, NullLogger<DecompilerProcessService>.Instance);
            var decompiled = new DecompiledArtifactService(Store, worker, NullLogger<DecompiledArtifactService>.Instance);
            Library = new EnvironmentCodeLibrary(Store, decompiled, new SnapshotUploadValidator(options), options, environment);
        }
        public async Task SeedCacheAsync(string? sourceOverride = null)
        {
            await Store.InitializeAsync();
            var source = sourceOverride ?? "class WorkOrderPlugin { void Execute() { WriteEquipment(); } void WriteEquipment() {\nvar apiKey = \"secret-value\";\nvar entity = new Entity(\"new_equipment\"); service.Create(entity); } }";
            var artifact = await Store.StoreArtifactAsync(new PreparedArtifact(ArtifactKind.DecompiledCSharp,
                "Business.decompiled.cs", AssemblyId, "1", "text/plain", Encoding.UTF8.GetBytes(source), null));
            var snapshotId = Guid.NewGuid();
            await Store.SaveSnapshotAsync(new StoredSnapshot(snapshotId, Guid.Empty, Context, [artifact], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            var hash = Convert.ToHexStringLower(SHA256.HashData(_dll));
            var directory = Path.Combine(_root, "data", "code-library-v1", "cache", EnvironmentCodeLibrary.EnvironmentKey(Context));
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, hash + ".json"), JsonSerializer.Serialize(
                new CodeLibraryEntry(AssemblyId, "Business.dll", hash, snapshotId, artifact.Name, DateTimeOffset.UtcNow, [], true, []),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
        public SnapshotUpload Upload(string fields)
        {
            var catalog = JsonSerializer.Serialize(new {
                assemblies = new[] { new { id = AssemblyId, name = "Business" } },
                types = new[] { new { id = "type-1", typeName = "WorkOrderPlugin", assemblyId = AssemblyId } },
                steps = new[] { new { id = "step-1", name = "WorkOrderUpdate", eventHandlerId = "type-1", messageId = "message-1", filterId = "filter-1", stage = 40, mode = 0, filteringAttributes = fields, stateCode = 0 } },
                messages = new[] { new { id = "message-1", name = "Update" } },
                filters = new[] { new { id = "filter-1", primaryObjectTypeCode = "new_workorder" } }
            });
            return new SnapshotUpload(Context, [
                TestArtifacts.Upload(ArtifactKind.PluginCatalog, catalog, "plugin-catalog.json"),
                new ArtifactUpload(ArtifactKind.PluginAssembly, "Business.dll", AssemblyId, "1", "application/octet-stream", Convert.ToBase64String(_dll))
            ], DateTimeOffset.UtcNow);
        }
        public async Task<Guid> ImportAsync(string fields, int expected = 1)
        {
            var result = await Library.ImportAsync(new CodeLibraryBatch(null, Upload(fields), expected, true), default);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(result));
            Assert.True(json.RootElement.GetProperty("cacheHit").GetBoolean());
            return json.RootElement.GetProperty("libraryId").GetGuid();
        }
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
    }
    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
