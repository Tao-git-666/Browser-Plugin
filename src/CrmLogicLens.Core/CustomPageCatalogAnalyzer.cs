using System.Text.Json;

namespace CrmLogicLens.Core;

public sealed class CustomPageCatalogAnalyzer
{
    public EvidenceGraph Analyze(DecodedArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var builder = new EvidenceGraphBuilder();
        if (artifact.Kind != ArtifactKind.CustomPageCatalog || artifact.Text is null)
        {
            builder.AddWarning($"{artifact.Name}: artifact is not custom-page catalog JSON text.");
            return builder.Build();
        }

        try
        {
            using var document = JsonDocument.Parse(artifact.Text, new JsonDocumentOptions { MaxDepth = 32 });
            if (!document.RootElement.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Array)
            {
                return builder.Build();
            }
            foreach (var page in pages.EnumerateArray().Take(24))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Read(page, "name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                var pageType = Read(page, "pageType") ?? "unknown";
                var displayName = Read(page, "displayName") ?? name;
                var pageId = EvidenceId.Create("custom-page", pageType, name);
                builder.AddNode(new EvidenceNode(
                    pageId,
                    "CustomPage",
                    displayName,
                    $"自定义页面 {name}（类型 {pageType}）。",
                    artifact.Name,
                    $"page {name}",
                    EvidenceConfidence.Confirmed,
                    EvidenceProperties.Create(("PageName", name), ("PageType", pageType))));

                if (!page.TryGetProperty("scripts", out var scripts) || scripts.ValueKind != JsonValueKind.Array) continue;
                foreach (var script in scripts.EnumerateArray().Take(90))
                {
                    var artifactName = Read(script, "artifactName");
                    if (string.IsNullOrWhiteSpace(artifactName)) continue;
                    var componentId = Read(script, "componentId");
                    var scriptId = EvidenceId.Create("javascript", componentId, artifactName);
                    builder.AddEdge(new EvidenceEdge(pageId, scriptId, "loads-script", EvidenceConfidence.Confirmed));
                }
            }
        }
        catch (JsonException exception)
        {
            builder.AddWarning($"{artifact.Name}: invalid custom-page catalog JSON ({exception.Message}).");
        }
        return builder.Build();
    }

    private static string? Read(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim()
            : null;
}
