using System.Text;

namespace CrmLogicLens.Core.Tests;

public sealed class PluginAndPipelineTests
{
    private const string Catalog = """
        {
          "steps": [{
            "id": "{AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA}",
            "name": "Account approval validation",
            "messageId": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
            "filterId": "cccccccc-cccc-cccc-cccc-cccccccccccc",
            "eventHandlerId": "dddddddd-dddd-dddd-dddd-dddddddddddd",
            "stage": 20, "mode": 0, "rank": 10,
            "filteringAttributes": "new_submitted,creditlimit", "stateCode": 0
          }],
          "messages": [{"id":"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb","name":"Update"}],
          "filters": [{"id":"cccccccc-cccc-cccc-cccc-cccccccccccc","primaryObjectTypeCode":"account"}],
          "types": [{
            "id":"dddddddd-dddd-dddd-dddd-dddddddddddd",
            "typeName":"Contoso.Crm.AccountApprovalPlugin",
            "name":"AccountApprovalPlugin",
            "assemblyId":"eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"
          }],
          "assemblies": [{
            "id":"eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee",
            "name":"Contoso.Crm.Plugins","version":"1.0.0.0","sourceType":0,"isolationMode":2
          }]
        }
        """;

    [Fact]
    public void PluginCatalog_BuildsStepToTypeToAssemblyEvidence()
    {
        var graph = new PluginCatalogAnalyzer().Analyze(
            TestArtifacts.Decode(ArtifactKind.PluginCatalog, Catalog, "plugin-catalog.json"));

        Assert.Contains(graph.Nodes, node => node.Kind == "PluginStep" &&
                                             node.Summary.Contains("预操作", StringComparison.Ordinal));
        Assert.Contains(graph.Edges, edge => edge.Relation == "handled-by");
        Assert.Contains(graph.Edges, edge => edge.Relation == "implemented-in");
        Assert.Contains(graph.Nodes, node => node.Kind == "PluginAssembly" &&
                                             Property(node, "SourceTypeName") == "Database");
    }

    [Fact]
    public void Pipeline_LinksWrappedJavaScriptCallToCustomApiImplementation()
    {
        const string catalog = """
            {
              "customApis":[{
                "id":"api-route-1","definitionId":"11111111-1111-1111-1111-111111111111",
                "uniqueName":"new_service","name":"Service API",
                "route":"CSSparePartInQuiry/CalculatePrice",
                "pluginTypeId":"dddddddd-dddd-dddd-dddd-dddddddddddd","source":"CustomApi"
              }],
              "steps":[],"messages":[],"filters":[],
              "types":[{
                "id":"dddddddd-dddd-dddd-dddd-dddddddddddd",
                "typeName":"Contoso.Crm.AccountApprovalPlugin",
                "assemblyId":"eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"
              }],
              "assemblies":[{
                "id":"eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee",
                "name":"Contoso.Crm.Plugins","sourceType":0
              }]
            }
            """;
        const string script = """
            function CalculatePrice_Click() {
              return rtcrm.invokeHiddenApiAsync("new_service", "CSSparePartInQuiry/CalculatePrice", { id: "1" });
            }
            """;
        var result = new AnalysisPipeline().Analyze(new SnapshotUpload(
            TestArtifacts.Context(),
            [
                TestArtifacts.Upload(ArtifactKind.JavaScript, script, "new_/logic.js"),
                TestArtifacts.Upload(ArtifactKind.PluginCatalog, catalog, "plugin-catalog.json")
            ],
            DateTimeOffset.UtcNow));

        Assert.Contains(result.Graph.Nodes, node => node.Kind == "CustomApi" &&
                                                    Property(node, "Route") == "CSSparePartInQuiry/CalculatePrice");
        Assert.Contains(result.Graph.Edges, edge => edge.Relation == "invokes-custom-api" &&
                                                   edge.Confidence == EvidenceConfidence.Confirmed);
        Assert.Contains(result.Graph.Edges, edge => edge.Relation == "implemented-by");
    }

    [Fact]
    public void PluginCatalog_ToleratesMissingFieldsAndMarksDiskAssemblyUnavailable()
    {
        const string json = """
            { "steps":[{"name":"partial"}], "types":[],
              "assemblies":[{"id":"disk-id","name":"DiskPlugin","sourceType":1}] }
            """;

        var graph = new PluginCatalogAnalyzer().Analyze(
            TestArtifacts.Decode(ArtifactKind.PluginCatalog, json, "partial.json"));

        Assert.Contains(graph.Nodes, node => node.Kind == "PluginStep" && node.Label == "partial");
        Assert.Contains(graph.Nodes, node => node.Kind == "PluginAssembly" &&
                                             Property(node, "ContentAvailability") == "UnavailableWithoutCrmServerFileAccess");
        Assert.Contains(graph.Warnings, warning => warning.Contains("SourceType=Disk", StringComparison.Ordinal));
    }

    [Fact]
    public void PluginCatalog_ReturnsWarningForMalformedJson()
    {
        var graph = new PluginCatalogAnalyzer().Analyze(
            TestArtifacts.Decode(ArtifactKind.PluginCatalog, "{\"steps\":[", "bad.json"));

        Assert.NotEmpty(graph.Warnings);
        Assert.Empty(graph.Nodes);
    }

    [Fact]
    public void AssemblyInspector_ReadsManagedMetadataWithoutLoadingAssembly()
    {
        var bytes = File.ReadAllBytes(typeof(PluginAndPipelineTests).Assembly.Location);
        var artifact = new DecodedArtifact(
            ArtifactKind.PluginAssembly,
            "CrmLogicLens.Core.Tests.dll",
            "test-assembly",
            null,
            "application/octet-stream",
            bytes,
            null,
            null);

        var graph = new PluginAssemblyInspector().Analyze(artifact);

        Assert.Contains(graph.Nodes, node => node.Kind == "ManagedType" &&
                                             node.Label.EndsWith(nameof(PluginAndPipelineTests), StringComparison.Ordinal));
        Assert.DoesNotContain(graph.Nodes, node => node.Kind == "ManagedMethod");
        Assert.Contains(graph.Nodes, node => node.Kind == "PluginAssembly" &&
                                             Property(node, "InspectionMode") == "MetadataOnly" &&
                                             int.Parse(Property(node, "DeclaredMethodCount")!) > 0);

        var relevantOnly = new PluginAssemblyInspector().Analyze(
            artifact,
            sourceType: 0,
            relevantTypeNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                typeof(PluginAndPipelineTests).FullName!
            });
        Assert.Single(relevantOnly.Nodes, node => node.Kind == "ManagedType");
        Assert.DoesNotContain(relevantOnly.Nodes, node => node.Kind == "ManagedMethod");
    }

    [Fact]
    public void AssemblyInspector_ReportsDiskNoContentAndInvalidPe()
    {
        var disk = new DecodedArtifact(
            ArtifactKind.PluginAssembly, "disk.dll", "disk", null, "application/octet-stream",
            ReadOnlyMemory<byte>.Empty, null, null);
        var invalid = disk with { Name = "invalid.dll", Bytes = new byte[] { 1, 2, 3, 4 } };

        var diskGraph = new PluginAssemblyInspector().Analyze(disk, sourceType: 1);
        var invalidGraph = new PluginAssemblyInspector().Analyze(invalid);

        Assert.Contains(diskGraph.Nodes, node => node.Summary.Contains("磁盘部署", StringComparison.Ordinal));
        Assert.NotEmpty(diskGraph.Warnings);
        Assert.NotEmpty(invalidGraph.Warnings);
    }

    [Fact]
    public void Pipeline_LinksFormAndRibbonToJavaScriptAndJavaScriptToPluginStep()
    {
        const string form = """
            <form><formLibraries><Library name="new_/logic.js" /></formLibraries><events><event name="onload"><Handlers>
              <Handler functionName="Logic.onLoad" libraryName="new_/logic.js" enabled="true" />
            </Handlers></event></events></form>
            """;
        const string ribbon = """
            <RibbonDefinitions><CommandDefinitions><CommandDefinition Id="new.Submit"><Actions>
              <JavaScriptFunction FunctionName="Logic.submit" Library="$webresource:new_/logic.js" />
            </Actions></CommandDefinition></CommandDefinitions></RibbonDefinitions>
            """;
        const string script = """
            var Logic = (function(){
              function onLoad(ctx){ ctx.getFormContext().getAttribute("creditlimit").getValue(); }
              async function submit(){ await Xrm.WebApi.updateRecord("account", "id", {new_submitted:true}); }
              return {onLoad:onLoad,submit:submit};
            })();
            """;
        const string decompiled = """
            namespace Contoso.Crm;
            public class AccountApprovalPlugin : IPlugin {
              public void Execute(IServiceProvider provider) {
                IOrganizationService service = null;
                service.Update(new Entity("account"));
              }
            }
            """;
        var artifacts = new ArtifactUpload[]
        {
            TestArtifacts.Upload(ArtifactKind.FormXml, form, "account.form.xml", "form-1"),
            TestArtifacts.Upload(ArtifactKind.RibbonXml, ribbon, "account.ribbon.xml"),
            TestArtifacts.Upload(ArtifactKind.JavaScript, script, "new_/logic.js"),
            TestArtifacts.Upload(ArtifactKind.PluginCatalog, Catalog, "plugin-catalog.json"),
            TestArtifacts.Upload(ArtifactKind.DecompiledCSharp, decompiled, "AccountApprovalPlugin.cs", "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee")
        };
        var snapshotId = Guid.NewGuid();

        var result = new AnalysisPipeline().Analyze(
            new SnapshotUpload(TestArtifacts.Context(), artifacts, DateTimeOffset.UtcNow),
            snapshotId: snapshotId);

        Assert.Equal(snapshotId, result.SnapshotId);
        Assert.Contains(result.Graph.Edges, edge => edge.Relation == "implemented-by" &&
                                                   edge.Confidence == EvidenceConfidence.Confirmed);
        Assert.Contains(result.Graph.Edges, edge => edge.Relation == "may-trigger-plugin-step");
        Assert.Contains(result.Graph.Edges, edge => edge.Relation == "matches-decompiled-type");
        Assert.Contains("前端行为", result.PlainLanguageSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("托管方法", result.PlainLanguageSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void Pipeline_SkipsInvalidArtifactAndKeepsValidEvidence()
    {
        var invalid = new ArtifactUpload(
            ArtifactKind.JavaScript, "bad.js", null, null, "text/javascript", "not-base64");
        var valid = TestArtifacts.Upload(
            ArtifactKind.FormXml, "<form><events /></form>", "form.xml", "form-1");

        var result = new AnalysisPipeline().Analyze(new SnapshotUpload(
            TestArtifacts.Context(), [invalid, valid], DateTimeOffset.UtcNow));

        Assert.Contains(result.Graph.Nodes, node => node.Kind == "Form");
        Assert.Contains(result.Graph.Warnings, warning => warning.Contains("Base64", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AnswerService_ProducesChineseAnswerWithInlineAndStructuredCitations()
    {
        var result = new AnalysisPipeline().Analyze(new SnapshotUpload(
            TestArtifacts.Context(),
            [
                TestArtifacts.Upload(ArtifactKind.JavaScript,
                    "async function submit(){ await Xrm.WebApi.updateRecord(\"account\",\"id\",{new_submitted:true}); }",
                    "new_/logic.js"),
                TestArtifacts.Upload(ArtifactKind.PluginCatalog, Catalog, "plugin-catalog.json")
            ],
            DateTimeOffset.UtcNow));
        var request = new ChatRequest(result.SnapshotId, "提交时会更新什么字段并触发哪个插件？");

        var response = new EvidenceAnswerService().Answer(request, result.Graph);

        Assert.NotEqual(EvidenceConfidence.Unknown, response.Confidence);
        Assert.NotEmpty(response.Citations);
        Assert.Contains("【证据:", response.Answer, StringComparison.Ordinal);
        Assert.Contains("new_submitted", response.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(response.Citations, citation => response.Answer.Contains(citation.NodeId, StringComparison.Ordinal));
    }

    [Fact]
    public void AnswerService_IgnoresLegacyManagedMethodInventory()
    {
        var graph = new EvidenceGraph(
            [
                new EvidenceNode("page:1", "PageContext", "account", "当前实体是 account。", "page-context.json", null, EvidenceConfidence.Confirmed),
                new EvidenceNode("step:1", "PluginStep", "账户校验", "更新 account 时执行账户校验。", "plugin-catalog.json", null, EvidenceConfidence.Confirmed),
                new EvidenceNode("method:1", "ManagedMethod", "Newtonsoft.Json.Serialize", "DLL 声明托管方法。", "plugins.dll", null, EvidenceConfidence.Confirmed),
                new EvidenceNode("type:1", "ManagedType", "Newtonsoft.Json.JsonConvert", "DLL 声明托管类型。", "plugins.dll", null, EvidenceConfidence.Confirmed)
            ],
            [],
            []);

        var response = new EvidenceAnswerService().Answer(
            new ChatRequest(Guid.NewGuid(), "这个插件做什么？"),
            graph);

        Assert.Contains(response.Citations, citation => citation.NodeId == "step:1");
        Assert.DoesNotContain(response.Citations, citation => citation.NodeId is "method:1" or "type:1");
        Assert.DoesNotContain("Newtonsoft", response.Answer, StringComparison.Ordinal);
    }

    private static string? Property(EvidenceNode node, string key) =>
        node.Properties?.TryGetValue(key, out var value) == true ? value : null;
}
