using System.Xml;
using System.Xml.Linq;

namespace CrmLogicLens.Core;

public sealed class RibbonXmlAnalyzer
{
    public EvidenceGraph Analyze(DecodedArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var builder = new EvidenceGraphBuilder();
        if (artifact.Kind != ArtifactKind.RibbonXml || artifact.Text is null)
        {
            builder.AddWarning($"{artifact.Name}: artifact is not RibbonXML text.");
            return builder.Build();
        }

        try
        {
            var document = SecureXml.Parse(artifact.Text);
            AddRuleDefinitions(document, artifact, builder, cancellationToken);
            AddCommands(document, artifact, builder, cancellationToken);
            AddButtons(document, artifact, builder, cancellationToken);
        }
        catch (XmlException exception)
        {
            builder.AddWarning($"{artifact.Name}: invalid or unsafe RibbonXML ({exception.Message}).");
        }
        catch (InvalidOperationException exception)
        {
            builder.AddWarning($"{artifact.Name}: RibbonXML could not be analyzed ({exception.Message}).");
        }

        return builder.Build();
    }

    private static void AddCommands(
        XDocument document,
        DecodedArtifact artifact,
        EvidenceGraphBuilder builder,
        CancellationToken cancellationToken)
    {
        foreach (var command in SecureXml.Descendants(document, "CommandDefinition"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var commandName = SecureXml.Attribute(command, "Id");
            if (string.IsNullOrWhiteSpace(commandName))
            {
                continue;
            }

            var commandId = EvidenceId.Create("ribbon-command", artifact.Name, commandName);
            builder.AddNode(new EvidenceNode(
                commandId,
                "RibbonCommand",
                commandName,
                $"Ribbon 命令 {commandName}。",
                artifact.Name,
                SecureXml.Location(command, $"command {commandName}"),
                EvidenceConfidence.Confirmed,
                EvidenceProperties.Create(("Command", commandName))));

            foreach (var function in SecureXml.Descendants(command, "JavaScriptFunction"))
            {
                var functionName = SecureXml.Attribute(function, "FunctionName");
                if (string.IsNullOrWhiteSpace(functionName))
                {
                    continue;
                }

                var library = NormalizeLibrary(SecureXml.Attribute(function, "Library"));
                var parameters = function.Elements()
                    .Select(DescribeParameter)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .ToArray();
                var actionId = EvidenceId.Create(
                    "ribbon-action",
                    artifact.Name,
                    commandName,
                    library,
                    functionName,
                    string.Join(",", parameters));
                builder.AddNode(new EvidenceNode(
                    actionId,
                    "RibbonJavaScriptAction",
                    functionName,
                    $"命令 {commandName} 调用 {functionName}（库 {library ?? "未声明"}）。",
                    artifact.Name,
                    SecureXml.Location(function, $"JavaScriptFunction {functionName}"),
                    EvidenceConfidence.Confirmed,
                    EvidenceProperties.Create(
                        ("Command", commandName),
                        ("FunctionName", functionName),
                        ("Library", library),
                        ("Parameters", string.Join(", ", parameters)))));
                builder.AddEdge(new EvidenceEdge(commandId, actionId, "executes", EvidenceConfidence.Confirmed));
            }

            AddRuleReferences(command, artifact, commandId, "EnableRule", "enabled-when", builder);
            AddRuleReferences(command, artifact, commandId, "DisplayRule", "visible-when", builder);
        }
    }

    private static void AddRuleReferences(
        XElement command,
        DecodedArtifact artifact,
        string commandId,
        string ruleElementName,
        string relation,
        EvidenceGraphBuilder builder)
    {
        foreach (var ruleReference in command.Descendants().Where(element => SecureXml.HasName(element, ruleElementName)))
        {
            var ruleName = SecureXml.Attribute(ruleReference, "Id");
            if (string.IsNullOrWhiteSpace(ruleName))
            {
                continue;
            }

            var ruleKind = ruleElementName.StartsWith("Enable", StringComparison.OrdinalIgnoreCase)
                ? "Enable"
                : "Display";
            var ruleId = EvidenceId.Create("ribbon-rule", artifact.Name, ruleKind, ruleName);
            builder.AddNode(new EvidenceNode(
                ruleId,
                "RibbonRule",
                ruleName,
                $"{ruleKind} 规则 {ruleName}（当前片段仅显示引用）。",
                artifact.Name,
                SecureXml.Location(ruleReference, $"{ruleKind} rule reference {ruleName}"),
                EvidenceConfidence.Confirmed,
                EvidenceProperties.Create(("RuleId", ruleName), ("RuleKind", ruleKind), ("DefinitionPresent", false))));
            builder.AddEdge(new EvidenceEdge(commandId, ruleId, relation, EvidenceConfidence.Confirmed));
        }
    }

    private static void AddRuleDefinitions(
        XDocument document,
        DecodedArtifact artifact,
        EvidenceGraphBuilder builder,
        CancellationToken cancellationToken)
    {
        foreach (var rule in document.Descendants().Where(element =>
                     (SecureXml.HasName(element, "EnableRule") || SecureXml.HasName(element, "DisplayRule")) &&
                     !element.Ancestors().Any(ancestor => SecureXml.HasName(ancestor, "CommandDefinition"))))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ruleName = SecureXml.Attribute(rule, "Id");
            if (string.IsNullOrWhiteSpace(ruleName))
            {
                continue;
            }

            var ruleKind = SecureXml.HasName(rule, "EnableRule") ? "Enable" : "Display";
            var conditions = rule.Elements().Select(DescribeCondition)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Take(30)
                .ToArray();
            var conditionText = conditions.Length == 0 ? "未包含可识别条件" : string.Join("；", conditions);
            var ruleId = EvidenceId.Create("ribbon-rule", artifact.Name, ruleKind, ruleName);
            builder.AddNode(new EvidenceNode(
                ruleId,
                "RibbonRule",
                ruleName,
                $"{ruleKind} 规则 {ruleName}：{conditionText}。",
                artifact.Name,
                SecureXml.Location(rule, $"{ruleKind} rule {ruleName}"),
                EvidenceConfidence.Confirmed,
                EvidenceProperties.Create(
                    ("RuleId", ruleName),
                    ("RuleKind", ruleKind),
                    ("Conditions", conditionText),
                    ("DefinitionPresent", true))));
        }
    }

    private static void AddButtons(
        XDocument document,
        DecodedArtifact artifact,
        EvidenceGraphBuilder builder,
        CancellationToken cancellationToken)
    {
        foreach (var control in document.Descendants().Where(element =>
                     SecureXml.HasName(element, "Button") ||
                     SecureXml.HasName(element, "SplitButton") ||
                     SecureXml.HasName(element, "FlyoutAnchor")))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var commandName = SecureXml.Attribute(control, "Command");
            if (string.IsNullOrWhiteSpace(commandName))
            {
                continue;
            }

            var controlId = SecureXml.Attribute(control, "Id") ?? commandName;
            var label = SecureXml.Attribute(control, "LabelText") ??
                        SecureXml.Attribute(control, "ToolTipTitle") ?? controlId;
            var buttonId = EvidenceId.Create("ribbon-button", artifact.Name, controlId);
            var commandId = EvidenceId.Create("ribbon-command", artifact.Name, commandName);
            builder.AddNode(new EvidenceNode(
                buttonId,
                "RibbonButton",
                label,
                $"按钮 {label} 绑定命令 {commandName}。",
                artifact.Name,
                SecureXml.Location(control, $"button {controlId}"),
                EvidenceConfidence.Confirmed,
                EvidenceProperties.Create(
                    ("ControlId", controlId),
                    ("Command", commandName),
                    ("ControlType", control.Name.LocalName))));
            builder.AddEdge(new EvidenceEdge(buttonId, commandId, "binds-command", EvidenceConfidence.Confirmed));
        }
    }

    private static string? NormalizeLibrary(string? library)
    {
        const string prefix = "$webresource:";
        return library?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true
            ? library[prefix.Length..]
            : library;
    }

    private static string DescribeParameter(XElement element)
    {
        var attributes = string.Join(",", element.Attributes().Select(attribute =>
            $"{attribute.Name.LocalName}={attribute.Value}"));
        return string.IsNullOrWhiteSpace(attributes)
            ? element.Name.LocalName
            : $"{element.Name.LocalName}({attributes})";
    }

    private static string DescribeCondition(XElement element)
    {
        var attributes = string.Join(", ", element.Attributes().Select(attribute =>
            $"{attribute.Name.LocalName}={attribute.Value}"));
        return string.IsNullOrWhiteSpace(attributes)
            ? element.Name.LocalName
            : $"{element.Name.LocalName}({attributes})";
    }
}
