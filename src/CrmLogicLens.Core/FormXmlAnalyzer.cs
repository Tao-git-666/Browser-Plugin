using System.Xml;
using System.Xml.Linq;

namespace CrmLogicLens.Core;

public sealed class FormXmlAnalyzer
{
    public EvidenceGraph Analyze(DecodedArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var builder = new EvidenceGraphBuilder();
        if (artifact.Kind != ArtifactKind.FormXml || artifact.Text is null)
        {
            builder.AddWarning($"{artifact.Name}: artifact is not FormXML text.");
            return builder.Build();
        }

        try
        {
            var document = SecureXml.Parse(artifact.Text);
            cancellationToken.ThrowIfCancellationRequested();

            var formId = EvidenceId.Create("form", artifact.ComponentId, artifact.Name);
            builder.AddNode(new EvidenceNode(
                formId,
                "Form",
                artifact.Name,
                "当前快照中的 CRM 窗体定义。",
                artifact.Name,
                document.Root is null ? null : SecureXml.Location(document.Root, "form"),
                EvidenceConfidence.Confirmed,
                EvidenceProperties.Create(
                    ("ComponentId", artifact.ComponentId),
                    ("Version", artifact.Version))));

            AddLibraries(document, artifact, formId, builder, cancellationToken);
            AddControls(document, artifact, formId, builder, cancellationToken);
            AddEvents(document, artifact, formId, builder, cancellationToken);
        }
        catch (XmlException exception)
        {
            builder.AddWarning($"{artifact.Name}: invalid or unsafe FormXML ({exception.Message}).");
        }
        catch (InvalidOperationException exception)
        {
            builder.AddWarning($"{artifact.Name}: FormXML could not be analyzed ({exception.Message}).");
        }

        return builder.Build();
    }

    private static void AddControls(
        XDocument document,
        DecodedArtifact artifact,
        string formId,
        EvidenceGraphBuilder builder,
        CancellationToken cancellationToken)
    {
        foreach (var control in SecureXml.Descendants(document, "control"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var field = SecureXml.Attribute(control, "datafieldname");
            if (string.IsNullOrWhiteSpace(field))
            {
                continue;
            }

            var hiddenOwner = control.AncestorsAndSelf().FirstOrDefault(element =>
                IsFalse(SecureXml.Attribute(element, "visible")));
            var staticallyVisible = hiddenOwner is null;
            var hiddenScope = hiddenOwner is null
                ? null
                : hiddenOwner == control
                    ? "control"
                    : hiddenOwner.Name.LocalName;
            var fieldId = EvidenceId.Create("field", artifact.Name, field);
            builder.AddNode(new EvidenceNode(
                fieldId,
                "Field",
                field,
                staticallyVisible
                    ? $"窗体包含字段 {field}，静态窗体配置未将它隐藏。"
                    : $"窗体包含字段 {field}，但它所在的 {hiddenScope} 在静态窗体配置中被隐藏。",
                artifact.Name,
                SecureXml.Location(control, $"field {field}"),
                EvidenceConfidence.Confirmed,
                EvidenceProperties.Create(
                    ("Field", field),
                    ("ControlId", SecureXml.Attribute(control, "id")),
                    ("StaticVisible", staticallyVisible),
                    ("HiddenScope", hiddenScope),
                    ("Disabled", SecureXml.Attribute(control, "disabled")))));
            builder.AddEdge(new EvidenceEdge(formId, fieldId, "contains-field", EvidenceConfidence.Confirmed));
        }
    }

    private static bool IsFalse(string? value) =>
        string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) || value == "0";

    private static void AddLibraries(
        XDocument document,
        DecodedArtifact artifact,
        string formId,
        EvidenceGraphBuilder builder,
        CancellationToken cancellationToken)
    {
        foreach (var library in SecureXml.Descendants(document, "Library"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = SecureXml.Attribute(library, "name") ?? SecureXml.Attribute(library, "libraryName");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var id = EvidenceId.Create("form-library", artifact.Name, name);
            builder.AddNode(new EvidenceNode(
                id,
                "FormLibrary",
                name,
                $"窗体引用 JavaScript 库 {name}。",
                artifact.Name,
                SecureXml.Location(library, $"library {name}"),
                EvidenceConfidence.Confirmed,
                EvidenceProperties.Create(
                    ("LibraryName", name),
                    ("LibraryUniqueId", SecureXml.Attribute(library, "libraryUniqueId")))));
            builder.AddEdge(new EvidenceEdge(formId, id, "references-library", EvidenceConfidence.Confirmed));
        }
    }

    private static void AddEvents(
        XDocument document,
        DecodedArtifact artifact,
        string formId,
        EvidenceGraphBuilder builder,
        CancellationToken cancellationToken)
    {
        var fieldIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var eventElement in SecureXml.Descendants(document, "event"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var eventName = SecureXml.Attribute(eventElement, "name") ?? "unknown";
            var control = eventElement.Ancestors()
                .FirstOrDefault(element => SecureXml.HasName(element, "control"));
            var field = control is null
                ? null
                : SecureXml.Attribute(control, "datafieldname") ?? SecureXml.Attribute(control, "id");
            var scope = string.IsNullOrWhiteSpace(field) ? "form" : $"field:{field}";
            var eventId = EvidenceId.Create("form-event", artifact.Name, scope, eventName);

            builder.AddNode(new EvidenceNode(
                eventId,
                "FormEvent",
                string.IsNullOrWhiteSpace(field) ? eventName : $"{field}.{eventName}",
                string.IsNullOrWhiteSpace(field)
                    ? $"窗体事件 {eventName}。"
                    : $"字段 {field} 的 {eventName} 事件。",
                artifact.Name,
                SecureXml.Location(eventElement, $"{scope}/{eventName}"),
                EvidenceConfidence.Confirmed,
                EvidenceProperties.Create(
                    ("Event", eventName),
                    ("Field", field),
                    ("Active", SecureXml.Attribute(eventElement, "active")))));
            builder.AddEdge(new EvidenceEdge(formId, eventId, "contains-event", EvidenceConfidence.Confirmed));

            if (!string.IsNullOrWhiteSpace(field))
            {
                if (!fieldIds.TryGetValue(field, out var fieldId))
                {
                    fieldId = EvidenceId.Create("field", artifact.Name, field);
                    fieldIds[field] = fieldId;
                    builder.AddNode(new EvidenceNode(
                        fieldId,
                        "Field",
                        field,
                        $"窗体字段 {field}。",
                        artifact.Name,
                        control is null ? null : SecureXml.Location(control, $"field {field}"),
                        EvidenceConfidence.Confirmed,
                        EvidenceProperties.Create(("Field", field))));
                    builder.AddEdge(new EvidenceEdge(formId, fieldId, "contains-field", EvidenceConfidence.Confirmed));
                }

                builder.AddEdge(new EvidenceEdge(fieldId, eventId, "owns-event", EvidenceConfidence.Confirmed));
            }

            AddHandlers(eventElement, artifact, eventId, eventName, field, builder);
        }
    }

    private static void AddHandlers(
        XElement eventElement,
        DecodedArtifact artifact,
        string eventId,
        string eventName,
        string? field,
        EvidenceGraphBuilder builder)
    {
        foreach (var handler in eventElement.Descendants().Where(element => SecureXml.HasName(element, "Handler")))
        {
            var functionName = SecureXml.Attribute(handler, "functionName");
            if (string.IsNullOrWhiteSpace(functionName))
            {
                continue;
            }

            var libraryName = SecureXml.Attribute(handler, "libraryName") ?? string.Empty;
            var enabledText = SecureXml.Attribute(handler, "enabled");
            var enabled = !string.Equals(enabledText, "false", StringComparison.OrdinalIgnoreCase) && enabledText != "0";
            var handlerUniqueId = SecureXml.Attribute(handler, "handlerUniqueId");
            var handlerId = EvidenceId.Create(
                "form-handler",
                artifact.Name,
                handlerUniqueId,
                field,
                eventName,
                libraryName,
                functionName);
            var scopeText = string.IsNullOrWhiteSpace(field) ? "窗体" : $"字段 {field}";
            builder.AddNode(new EvidenceNode(
                handlerId,
                "FormHandler",
                functionName,
                $"{scopeText}的 {eventName} 事件{(enabled ? "会" : "不会（处理器已禁用）")}调用 {functionName}。",
                artifact.Name,
                SecureXml.Location(handler, $"handler {functionName}"),
                EvidenceConfidence.Confirmed,
                EvidenceProperties.Create(
                    ("FunctionName", functionName),
                    ("Library", libraryName),
                    ("Event", eventName),
                    ("Field", field),
                    ("Enabled", enabled),
                    ("PassExecutionContext", SecureXml.Attribute(handler, "passExecutionContext")),
                    ("HandlerUniqueId", handlerUniqueId))));
            builder.AddEdge(new EvidenceEdge(eventId, handlerId, "invokes-handler", EvidenceConfidence.Confirmed));

            if (!string.IsNullOrWhiteSpace(libraryName))
            {
                var libraryId = EvidenceId.Create("form-library", artifact.Name, libraryName);
                builder.AddNode(new EvidenceNode(
                    libraryId,
                    "FormLibrary",
                    libraryName,
                    $"事件处理器引用 JavaScript 库 {libraryName}。",
                    artifact.Name,
                    SecureXml.Location(handler, $"library reference {libraryName}"),
                    EvidenceConfidence.Confirmed,
                    EvidenceProperties.Create(("LibraryName", libraryName))));
                builder.AddEdge(new EvidenceEdge(handlerId, libraryId, "uses-library", EvidenceConfidence.Confirmed));
            }
        }
    }
}
