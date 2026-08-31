using System.Text;

namespace CrmLogicLens.Core.Tests;

internal static class TestArtifacts
{
    public static DecodedArtifact Decode(
        ArtifactKind kind,
        string text,
        string name,
        string? componentId = null,
        string? mediaType = null)
    {
        var upload = Upload(kind, text, name, componentId, mediaType);
        return new ArtifactDecoder().Decode(upload);
    }

    public static ArtifactUpload Upload(
        ArtifactKind kind,
        string text,
        string name,
        string? componentId = null,
        string? mediaType = null) => new(
        kind,
        name,
        componentId,
        null,
        mediaType ?? kind switch
        {
            ArtifactKind.FormXml or ArtifactKind.RibbonXml => "application/xml",
            ArtifactKind.JavaScript => "application/javascript",
            ArtifactKind.PluginCatalog or ArtifactKind.EntityMetadata => "application/json",
            ArtifactKind.DecompiledCSharp => "text/x-csharp",
            _ => "application/octet-stream"
        },
        Convert.ToBase64String(Encoding.UTF8.GetBytes(text)));

    public static CrmPageContext Context(string entity = "account", string formId = "form-1") => new(
        "https://crm.contoso.local/Contoso",
        "org-1",
        "9.1.0",
        "v9.1",
        "entityrecord",
        entity,
        "record-1",
        formId,
        "app-1",
        "客户窗体");
}
