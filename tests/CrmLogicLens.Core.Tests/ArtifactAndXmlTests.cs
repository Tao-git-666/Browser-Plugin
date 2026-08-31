using System.Text;

namespace CrmLogicLens.Core.Tests;

public sealed class ArtifactAndXmlTests
{
    [Fact]
    public void Decoder_DecodesStrictUtf8AndNormalizesMediaType()
    {
        var upload = TestArtifacts.Upload(ArtifactKind.JavaScript, "const 值 = 1;", "logic.js") with
        {
            MediaType = "Application/JavaScript; charset=utf-8"
        };

        var decoded = new ArtifactDecoder().Decode(upload);

        Assert.Equal("const 值 = 1;", decoded.Text);
        Assert.Equal("application/javascript", decoded.MediaType);
    }

    [Fact]
    public void Decoder_RejectsOversizedInputBeforeAcceptingIt()
    {
        var decoder = new ArtifactDecoder(new ArtifactDecoderOptions
        {
            MaxTextArtifactBytes = 8,
            MaxPluginAssemblyBytes = 16,
            MaxArtifactNameLength = 100,
            MaxEncodedWhitespace = 10,
            MaxArtifactsPerSnapshot = 2,
            MaxSnapshotBytes = 32
        });
        var upload = TestArtifacts.Upload(ArtifactKind.JavaScript, new string('a', 100), "large.js");

        var exception = Assert.Throws<ArtifactValidationException>(() => decoder.Decode(upload));

        Assert.Contains("limit", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("not base64!!")]
    [InlineData("AA==")]
    public void Decoder_RejectsInvalidBase64OrInvalidUtf8(string encoded)
    {
        var upload = new ArtifactUpload(
            ArtifactKind.JavaScript, "bad.js", null, null, "text/javascript", encoded);

        Assert.Throws<ArtifactValidationException>(() => new ArtifactDecoder().Decode(upload));
    }

    [Fact]
    public void Decoder_AllowsEmptyAssemblyForDiskCatalogEntries()
    {
        var upload = new ArtifactUpload(
            ArtifactKind.PluginAssembly, "disk.dll", "assembly-id", null,
            "application/octet-stream", string.Empty);

        var decoded = new ArtifactDecoder().Decode(upload);

        Assert.True(decoded.Bytes.IsEmpty);
        Assert.Null(decoded.Text);
    }

    [Fact]
    public void FormAnalyzer_ExtractsLibrariesFormEventsAndFieldEvents()
    {
        const string xml = """
            <form>
              <formLibraries><Library name="new_/logic.js" /></formLibraries>
              <events><event name="onload" active="true"><Handlers>
                <Handler functionName="Logic.onLoad" libraryName="new_/logic.js" enabled="true" passExecutionContext="true" />
              </Handlers></event></events>
              <control id="creditlimit" datafieldname="creditlimit"><events>
                <event name="onchange"><Handlers><Handler functionName="Logic.changed" libraryName="new_/logic.js" /></Handlers></event>
              </events></control>
            </form>
            """;

        var graph = new FormXmlAnalyzer().Analyze(
            TestArtifacts.Decode(ArtifactKind.FormXml, xml, "account.form.xml", "form-1"));

        Assert.Contains(graph.Nodes, node => node.Kind == "FormLibrary" && node.Label == "new_/logic.js");
        Assert.Contains(graph.Nodes, node => node.Kind == "FormHandler" && node.Label == "Logic.onLoad");
        Assert.Contains(graph.Nodes, node => node.Kind == "FormEvent" && node.Label == "creditlimit.onchange");
        Assert.Contains(graph.Edges, edge => edge.Relation == "uses-library");
        Assert.Empty(graph.Warnings);
    }

    [Fact]
    public void FormAnalyzer_IndexesControlsWithoutEventsAndInheritedVisibility()
    {
        const string xml = """
            <form><tabs><tab><columns><column><sections><section><rows><row>
              <cell visible="false"><control id="hidden_solution" datafieldname="new_solution" /></cell>
              <cell><control id="visible_status" datafieldname="new_planstatus" /></cell>
            </row></rows></section></sections></column></columns></tab></tabs></form>
            """;

        var graph = new FormXmlAnalyzer().Analyze(
            TestArtifacts.Decode(ArtifactKind.FormXml, xml, "plan.form.xml", "form-2"));

        var hidden = Assert.Single(graph.Nodes, node => node.Kind == "Field" && node.Label == "new_solution");
        var visible = Assert.Single(graph.Nodes, node => node.Kind == "Field" && node.Label == "new_planstatus");
        Assert.Equal("False", hidden.Properties?["StaticVisible"]);
        Assert.Equal("cell", hidden.Properties?["HiddenScope"]);
        Assert.Equal("True", visible.Properties?["StaticVisible"]);
        Assert.Contains(graph.Edges, edge => edge.SourceId.StartsWith("form:", StringComparison.Ordinal) &&
                                             edge.TargetId == hidden.Id && edge.Relation == "contains-field");
    }

    [Fact]
    public void FormAnalyzer_RejectsDtdAndExternalEntityInput()
    {
        const string xml = """
            <!DOCTYPE form [<!ENTITY xxe SYSTEM "file:///windows/win.ini">]>
            <form><events><event name="onload"><Handlers><Handler functionName="&xxe;" /></Handlers></event></events></form>
            """;

        var graph = new FormXmlAnalyzer().Analyze(
            TestArtifacts.Decode(ArtifactKind.FormXml, xml, "unsafe.xml"));

        Assert.Empty(graph.Nodes);
        Assert.Contains(graph.Warnings, warning => warning.Contains("unsafe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RibbonAnalyzer_ExtractsButtonsCommandsActionsAndRuleDefinitions()
    {
        const string xml = """
            <RibbonDefinitions>
              <RuleDefinitions><EnableRules><EnableRule Id="new.Rule.Record"><EntityRule EntityName="account" /></EnableRule></EnableRules></RuleDefinitions>
              <CommandDefinitions><CommandDefinition Id="new.Command.Submit">
                <EnableRules><EnableRule Id="new.Rule.Record" /></EnableRules>
                <Actions><JavaScriptFunction FunctionName="Logic.submit" Library="$webresource:new_/logic.js"><CrmParameter Value="PrimaryControl" /></JavaScriptFunction></Actions>
              </CommandDefinition></CommandDefinitions>
              <RibbonDiffXml><CustomActions><CustomAction><CommandUIDefinition><Button Id="new.Button.Submit" LabelText="提交审批" Command="new.Command.Submit" /></CommandUIDefinition></CustomAction></CustomActions></RibbonDiffXml>
            </RibbonDefinitions>
            """;

        var graph = new RibbonXmlAnalyzer().Analyze(
            TestArtifacts.Decode(ArtifactKind.RibbonXml, xml, "account.ribbon.xml"));

        Assert.Contains(graph.Nodes, node => node.Kind == "RibbonButton" && node.Label == "提交审批");
        Assert.Contains(graph.Nodes, node => node.Kind == "RibbonJavaScriptAction" && node.Label == "Logic.submit");
        Assert.Contains(graph.Nodes, node => node.Kind == "RibbonRule" &&
                                             node.Summary.Contains("EntityRule", StringComparison.Ordinal));
        Assert.Contains(graph.Edges, edge => edge.Relation == "binds-command");
        Assert.Contains(graph.Edges, edge => edge.Relation == "enabled-when");
    }
}
