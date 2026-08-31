namespace CrmLogicLens.Core.Tests;

public sealed class CodeAnalyzerTests
{
    [Fact]
    public void JavaScriptAnalyzer_UsesAstToExtractD365Operations()
    {
        const string script = """
            var Logic = (function () {
              function onLoad(executionContext) {
                var formContext = executionContext.getFormContext();
                var status = formContext.getAttribute("statuscode").getValue();
                formContext.getAttribute("description").setRequiredLevel("required");
                formContext.getControl("creditlimit").setVisible(status === 1);
                formContext.getControl("creditlimit").setDisabled(false);
                formContext.getAttribute("creditlimit").setValue(10);
                formContext.data.entity.save();
              }
              async function submit() {
                await Xrm.WebApi.updateRecord("account", "id", { new_submitted: true, creditlimit: 5 });
                await Xrm.WebApi.retrieveMultipleRecords("account", "?fetchXml=<fetch />");
              }
              return { onLoad: onLoad, submit: submit };
            })();
            """;

        var graph = new JavaScriptAnalyzer().Analyze(
            TestArtifacts.Decode(ArtifactKind.JavaScript, script, "new_/logic.js"));
        var behaviors = graph.Nodes.Where(node => node.Kind == "JavaScriptBehavior").ToArray();

        Assert.Contains(behaviors, node => Property(node, "Behavior") == "FieldRead" && Property(node, "Subject") == "statuscode");
        Assert.Contains(behaviors, node => Property(node, "Behavior") == "FieldRequiredLevel");
        Assert.Contains(behaviors, node => Property(node, "Behavior") == "ControlVisibility");
        Assert.Contains(behaviors, node => Property(node, "Behavior") == "ControlDisabledState");
        Assert.Contains(behaviors, node => Property(node, "Behavior") == "FieldWrite");
        Assert.Contains(behaviors, node => Property(node, "Behavior") == "FormSave");
        Assert.Contains(behaviors, node => Property(node, "Operation") == "Update" &&
                                           Property(node, "Fields")!.Contains("new_submitted", StringComparison.Ordinal));
        Assert.Contains(behaviors, node => Property(node, "Behavior") == "FetchXmlQuery");
        Assert.Contains(graph.Nodes, node => node.Kind == "JavaScriptFunction" && node.Label == "Logic.onLoad");
        Assert.Empty(graph.Warnings);
    }

    [Fact]
    public void JavaScriptAnalyzer_RejectsMalformedAndExcessivelyNestedScriptsWithoutExecutingThem()
    {
        var malformed = new JavaScriptAnalyzer().Analyze(
            TestArtifacts.Decode(ArtifactKind.JavaScript, "function () {", "bad.js"));
        var nested = new JavaScriptAnalyzer().Analyze(
            TestArtifacts.Decode(ArtifactKind.JavaScript, new string('(', 500) + "1" + new string(')', 500), "deep.js"));

        Assert.NotEmpty(malformed.Warnings);
        Assert.NotEmpty(nested.Warnings);
        Assert.Empty(nested.Nodes);
    }

    [Fact]
    public void JavaScriptAnalyzer_RecognizesWrappedCustomApiAndBusinessRoute()
    {
        const string script = """
            function CalculatePrice_Click() {
              return rtcrm.invokeHiddenApiAsync(
                "new_service",
                "CSSparePartInQuiry/CalculatePrice",
                { new_cs_sparepartinquiryid: "id" });
            }
            """;

        var graph = new JavaScriptAnalyzer().Analyze(
            TestArtifacts.Decode(ArtifactKind.JavaScript, script, "new_/sparepart.js"));

        var behavior = Assert.Single(graph.Nodes, node =>
            node.Kind == "JavaScriptBehavior" && Property(node, "Behavior") == "CustomApiAction");
        Assert.Equal("new_service", Property(behavior, "Operation"));
        Assert.Equal("CSSparePartInQuiry/CalculatePrice", Property(behavior, "Route"));
        Assert.Equal("new_cs_sparepartinquiryid", Property(behavior, "Fields"));
        Assert.Contains(graph.Edges, edge => edge.Relation == "performs" && edge.TargetId == behavior.Id);
    }

    [Fact]
    public void JavaScriptAnalyzer_RecognizesOpenedWebResourcesAndCustomPages()
    {
        const string script = """
            function dispatch() {
              Xrm.Navigation.openWebResource("new_/dispatch/index.html");
              return Xrm.Navigation.navigateTo({ pageType: "custom", name: "new_dispatchpage" });
            }
            """;

        var graph = new JavaScriptAnalyzer().Analyze(
            TestArtifacts.Decode(ArtifactKind.JavaScript, script, "new_/dispatch.js"));
        var openings = graph.Nodes.Where(node =>
            node.Kind == "JavaScriptBehavior" && Property(node, "Behavior") == "OpenCustomPage").ToArray();

        Assert.Contains(openings, node =>
            Property(node, "TargetName") == "new_/dispatch/index.html" &&
            Property(node, "PageType") == "webresource");
        Assert.Contains(openings, node =>
            Property(node, "TargetName") == "new_dispatchpage" &&
            Property(node, "PageType") == "custom");
    }

    [Fact]
    public void CustomPageCatalogAnalyzer_LinksPageToItsScripts()
    {
        const string catalog = """
            {"pages":[{"name":"new_/dispatch/index.html","pageType":"webresource","displayName":"派工",
              "scripts":[{"artifactName":"new_/dispatch/page.js","componentId":"script-1"}]}]}
            """;

        var graph = new CustomPageCatalogAnalyzer().Analyze(
            TestArtifacts.Decode(ArtifactKind.CustomPageCatalog, catalog, "new_ticket.custom-pages.json"));

        var page = Assert.Single(graph.Nodes, node => node.Kind == "CustomPage");
        Assert.Equal("new_/dispatch/index.html", Property(page, "PageName"));
        Assert.Contains(graph.Edges, edge => edge.SourceId == page.Id && edge.Relation == "loads-script");
    }

    [Fact]
    public void CSharpAnalyzer_ExtractsPluginEntryContextServiceCallsExceptionAndExternalAccess()
    {
        const string csharp = """
            namespace Contoso.Crm;
            public sealed class AccountApprovalPlugin : IPlugin
            {
                public void Execute(IServiceProvider serviceProvider)
                {
                    IPluginExecutionContext context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
                    IOrganizationService service = ((IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory))).CreateOrganizationService(context.UserId);
                    if (context.MessageName == "Update" && context.PrimaryEntityName == "account" && context.InputParameters.Contains("Target"))
                    {
                        Entity task = new Entity("task");
                        task["subject"] = "Approval";
                        service.Create(task);
                        using var client = new HttpClient();
                        throw new InvalidPluginExecutionException("审批失败");
                    }
                }
            }
            """;

        var graph = new CSharpPluginAnalyzer().Analyze(
            TestArtifacts.Decode(ArtifactKind.DecompiledCSharp, csharp, "AccountApprovalPlugin.cs", "assembly-1"));

        Assert.Contains(graph.Nodes, node => node.Kind == "CSharpPluginType" &&
                                             node.Label == "Contoso.Crm.AccountApprovalPlugin");
        Assert.Contains(graph.Nodes, node => node.Kind == "CSharpExecuteMethod");
        Assert.Contains(graph.Nodes, node => node.Kind == "CSharpPluginBehavior" && Property(node, "MessageName") == "Update");
        Assert.Contains(graph.Nodes, node => node.Kind == "CSharpPluginBehavior" && Property(node, "PrimaryEntityName") == "account");
        Assert.Contains(graph.Nodes, node => node.Kind == "CSharpPluginBehavior" && Property(node, "Parameter") == "Target");
        Assert.Contains(graph.Nodes, node => node.Kind == "CSharpPluginBehavior" &&
                                             Property(node, "ServiceOperation") == "Create" &&
                                             Property(node, "Entity") == "task" &&
                                             Property(node, "Fields") == "subject");
        Assert.Contains(graph.Nodes, node => node.Kind == "CSharpPluginBehavior" && Property(node, "Behavior") == "PluginException");
        Assert.Contains(graph.Nodes, node => node.Kind == "CSharpPluginBehavior" &&
                                             Property(node, "Behavior") == "ExternalCall" &&
                                             node.Confidence == EvidenceConfidence.Inferred);
        Assert.Empty(graph.Warnings);
    }

    private static string? Property(EvidenceNode node, string key) =>
        node.Properties?.TryGetValue(key, out var value) == true ? value : null;
}
