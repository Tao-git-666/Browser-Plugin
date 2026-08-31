using System.Text.Json;

namespace CrmLogicLens.Core;

public sealed class EntityMetadataAnalyzer
{
    public EvidenceGraph Analyze(DecodedArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var builder = new EvidenceGraphBuilder();
        if (artifact.Kind != ArtifactKind.EntityMetadata || artifact.Text is null)
        {
            builder.AddWarning($"{artifact.Name}: artifact is not entity-metadata JSON text.");
            return builder.Build();
        }

        try
        {
            using var document = JsonDocument.Parse(artifact.Text, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 64
            });
            var roots = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToArray()
                : [document.RootElement];
            foreach (var entity in roots.Where(element => element.ValueKind == JsonValueKind.Object))
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddEntity(entity, artifact, builder, cancellationToken);
            }
        }
        catch (JsonException exception)
        {
            builder.AddWarning($"{artifact.Name}: invalid entity metadata JSON ({exception.Message}).");
        }

        return builder.Build();
    }

    private static void AddEntity(
        JsonElement entity,
        DecodedArtifact artifact,
        EvidenceGraphBuilder builder,
        CancellationToken cancellationToken)
    {
        var logicalName = ReadString(entity, "LogicalName") ?? ReadString(entity, "logicalName") ?? artifact.ComponentId;
        if (string.IsNullOrWhiteSpace(logicalName))
        {
            return;
        }

        var label = ReadLabel(entity, "DisplayName") ?? logicalName;
        var entityId = EvidenceId.Create("entity-metadata", logicalName);
        builder.AddNode(new EvidenceNode(
            entityId,
            "EntityMetadata",
            label,
            $"实体 {logicalName} 的显示名称是“{label}”。",
            artifact.Name,
            $"entity {logicalName}",
            EvidenceConfidence.Confirmed,
            EvidenceProperties.Create(("Entity", logicalName), ("DisplayName", label))));

        if (!TryGet(entity, "Attributes", out var attributes) || attributes.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var attribute in attributes.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attribute.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var field = ReadString(attribute, "LogicalName") ?? ReadString(attribute, "logicalName");
            if (string.IsNullOrWhiteSpace(field))
            {
                continue;
            }

            var fieldLabel = ReadLabel(attribute, "DisplayName") ?? field;
            var fieldId = EvidenceId.Create("field-metadata", logicalName, field);
            builder.AddNode(new EvidenceNode(
                fieldId,
                "FieldMetadata",
                fieldLabel,
                $"字段 {field} 的显示名称是“{fieldLabel}”。",
                artifact.Name,
                $"entity {logicalName}/attribute {field}",
                EvidenceConfidence.Confirmed,
                EvidenceProperties.Create(
                    ("Entity", logicalName),
                    ("Field", field),
                    ("DisplayName", fieldLabel),
                    ("AttributeType", ReadString(attribute, "AttributeType")))));
            builder.AddEdge(new EvidenceEdge(entityId, fieldId, "defines-field", EvidenceConfidence.Confirmed));
            AddOptions(attribute, artifact, fieldId, logicalName, field, builder);
        }
    }

    private static void AddOptions(
        JsonElement attribute,
        DecodedArtifact artifact,
        string fieldId,
        string entity,
        string field,
        EvidenceGraphBuilder builder)
    {
        if (!TryGet(attribute, "OptionSet", out var optionSet) || optionSet.ValueKind != JsonValueKind.Object ||
            !TryGet(optionSet, "Options", out var options) || options.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var option in options.EnumerateArray())
        {
            if (option.ValueKind != JsonValueKind.Object || !TryGet(option, "Value", out var value))
            {
                continue;
            }

            var valueText = value.ValueKind == JsonValueKind.Number || value.ValueKind == JsonValueKind.String
                ? value.ToString()
                : null;
            if (string.IsNullOrWhiteSpace(valueText))
            {
                continue;
            }

            var label = ReadLabel(option, "Label") ?? valueText;
            var optionId = EvidenceId.Create("option-metadata", entity, field, valueText);
            builder.AddNode(new EvidenceNode(
                optionId,
                "OptionMetadata",
                label,
                $"字段 {field} 的选项值 {valueText} 表示“{label}”。",
                artifact.Name,
                $"attribute {field}/option {valueText}",
                EvidenceConfidence.Confirmed,
                EvidenceProperties.Create(
                    ("Entity", entity), ("Field", field), ("Value", valueText), ("Label", label))));
            builder.AddEdge(new EvidenceEdge(fieldId, optionId, "defines-option", EvidenceConfidence.Confirmed));
        }
    }

    private static string? ReadLabel(JsonElement element, string propertyName)
    {
        if (!TryGet(element, propertyName, out var label))
        {
            return null;
        }

        if (label.ValueKind == JsonValueKind.String)
        {
            return label.GetString();
        }

        if (label.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (TryGet(label, "UserLocalizedLabel", out var localized) && localized.ValueKind == JsonValueKind.Object)
        {
            return ReadString(localized, "Label");
        }

        if (TryGet(label, "LocalizedLabels", out var localizedLabels) && localizedLabels.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in localizedLabels.EnumerateArray())
            {
                var value = ReadString(item, "Label");
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        TryGet(element, propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryGet(JsonElement element, string propertyName, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
