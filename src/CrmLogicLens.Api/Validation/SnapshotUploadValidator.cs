using System.Net.Http.Headers;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using CrmLogicLens.Api.Configuration;
using CrmLogicLens.Api.Errors;
using CrmLogicLens.Api.Storage;
using CrmLogicLens.Core;
using Microsoft.Extensions.Options;

namespace CrmLogicLens.Api.Validation;

public sealed partial class SnapshotUploadValidator(IOptions<StorageOptions> options)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static readonly IReadOnlyDictionary<ArtifactKind, HashSet<string>> AllowedMediaTypes =
        new Dictionary<ArtifactKind, HashSet<string>>
        {
            [ArtifactKind.FormXml] = new(StringComparer.OrdinalIgnoreCase) { "application/xml", "text/xml" },
            [ArtifactKind.JavaScript] = new(StringComparer.OrdinalIgnoreCase)
            {
                "application/javascript", "text/javascript", "text/plain"
            },
            [ArtifactKind.RibbonXml] = new(StringComparer.OrdinalIgnoreCase) { "application/xml", "text/xml" },
            [ArtifactKind.PluginCatalog] = new(StringComparer.OrdinalIgnoreCase) { "application/json" },
            [ArtifactKind.PluginAssembly] = new(StringComparer.OrdinalIgnoreCase)
            {
                "application/octet-stream",
                "application/vnd.microsoft.portable-executable",
                "application/x-msdownload"
            },
            [ArtifactKind.EntityMetadata] = new(StringComparer.OrdinalIgnoreCase) { "application/json" },
            [ArtifactKind.CustomPageCatalog] = new(StringComparer.OrdinalIgnoreCase) { "application/json" }
        };

    private readonly StorageOptions _options = options.Value;

    public IReadOnlyDictionary<ArtifactKind, IReadOnlyCollection<string>> SupportedMediaTypes =>
        AllowedMediaTypes.ToDictionary(
            item => item.Key,
            item => (IReadOnlyCollection<string>)item.Value.Order(StringComparer.Ordinal).ToArray());

    public IReadOnlyList<PreparedArtifact> ValidateAndPrepare(SnapshotUpload? upload, DateTimeOffset now)
    {
        if (upload is null)
        {
            throw new ApiInputException("A snapshot payload is required.", "body");
        }

        ValidateContext(upload.Context);

        if (upload.CapturedAt == default || upload.CapturedAt > now.AddMinutes(10))
        {
            throw new ApiInputException(
                "capturedAt must be a valid timestamp and cannot be more than ten minutes in the future.",
                "capturedAt");
        }

        if (upload.Artifacts is null || upload.Artifacts.Count == 0)
        {
            throw new ApiInputException("At least one artifact is required.", "artifacts");
        }

        if (upload.Artifacts.Count > _options.MaxArtifactsPerSnapshot)
        {
            throw new ApiInputException(
                $"A snapshot can contain at most {_options.MaxArtifactsPerSnapshot} artifacts.",
                "artifacts",
                StatusCodes.Status413PayloadTooLarge);
        }

        var prepared = new List<PreparedArtifact>(upload.Artifacts.Count);
        var uniqueArtifacts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;

        for (var index = 0; index < upload.Artifacts.Count; index++)
        {
            var field = $"artifacts[{index}]";
            var artifact = upload.Artifacts[index]
                ?? throw new ApiInputException("An artifact cannot be null.", field);

            ValidateText(artifact.Name, 1, 256, $"{field}.name");
            ValidateOptionalText(artifact.ComponentId, 256, $"{field}.componentId");
            ValidateOptionalText(artifact.Version, 128, $"{field}.version");

            var uniquenessKey = $"{artifact.Kind}:{artifact.ComponentId ?? artifact.Name}";
            if (!uniqueArtifacts.Add(uniquenessKey))
            {
                throw new ApiInputException(
                    "The snapshot contains duplicate artifact identities.",
                    field);
            }

            var mediaType = ValidateMediaType(artifact, field);
            var content = DecodeBase64(artifact.ContentBase64, field);
            if (content.LongLength > _options.MaxArtifactBytes)
            {
                throw new ApiInputException(
                    $"An artifact cannot exceed {_options.MaxArtifactBytes} decoded bytes.",
                    $"{field}.contentBase64",
                    StatusCodes.Status413PayloadTooLarge);
            }

            totalBytes = checked(totalBytes + content.LongLength);
            if (totalBytes > _options.MaxSnapshotBytes)
            {
                throw new ApiInputException(
                    $"The decoded snapshot cannot exceed {_options.MaxSnapshotBytes} bytes.",
                    "artifacts",
                    StatusCodes.Status413PayloadTooLarge);
            }

            content = ValidateAndSanitizeContent(artifact.Kind, content, field);
            var sourceUrl = ValidateSourceUrl(artifact.SourceUrl, upload.Context.OrganizationUrl, field);

            prepared.Add(new PreparedArtifact(
                artifact.Kind,
                artifact.Name.Trim(),
                artifact.ComponentId?.Trim(),
                artifact.Version?.Trim(),
                mediaType,
                content,
                sourceUrl));
        }

        return prepared;
    }

    private static void ValidateContext(CrmPageContext? context)
    {
        if (context is null)
        {
            throw new ApiInputException("context is required.", "context");
        }

        _ = ParseOrganizationUri(context.OrganizationUrl);
        ValidateOptionalGuid(context.OrganizationId, "context.organizationId");
        ValidateOptionalText(context.Version, 64, "context.version");
        ValidateOptionalText(context.ApiVersion, 32, "context.apiVersion");
        ValidateText(context.PageType, 1, 64, "context.pageType");
        if (!IdentifierPattern().IsMatch(context.PageType))
        {
            throw new ApiInputException("pageType contains unsupported characters.", "context.pageType");
        }

        ValidateOptionalText(context.EntityName, 128, "context.entityName");
        if (context.EntityName is not null && !LogicalNamePattern().IsMatch(context.EntityName))
        {
            throw new ApiInputException("entityName is not a valid logical name.", "context.entityName");
        }

        ValidateOptionalGuid(context.EntityId, "context.entityId");
        ValidateOptionalGuid(context.FormId, "context.formId");
        ValidateOptionalGuid(context.AppId, "context.appId");
        ValidateOptionalText(context.FormLabel, 256, "context.formLabel");
    }

    private string ValidateMediaType(ArtifactUpload artifact, string field)
    {
        if (string.IsNullOrWhiteSpace(artifact.MediaType) ||
            !MediaTypeHeaderValue.TryParse(artifact.MediaType, out var parsed) ||
            string.IsNullOrWhiteSpace(parsed.MediaType))
        {
            throw new ApiInputException("mediaType is invalid.", $"{field}.mediaType");
        }

        if (parsed.CharSet is not null &&
            !string.Equals(parsed.CharSet, "utf-8", StringComparison.OrdinalIgnoreCase))
        {
            throw new ApiInputException("Only UTF-8 text artifacts are accepted.", $"{field}.mediaType");
        }

        var mediaType = parsed.MediaType.ToLowerInvariant();
        if (!AllowedMediaTypes.TryGetValue(artifact.Kind, out var allowed) || !allowed.Contains(mediaType))
        {
            throw new ApiInputException(
                $"The media type '{mediaType}' is not accepted for {artifact.Kind} artifacts.",
                $"{field}.mediaType",
                StatusCodes.Status415UnsupportedMediaType);
        }

        return mediaType;
    }

    private byte[] ValidateAndSanitizeContent(ArtifactKind kind, byte[] content, string field)
    {
        if (content.Length == 0)
        {
            throw new ApiInputException("Artifact content cannot be empty.", $"{field}.contentBase64");
        }

        try
        {
            return kind switch
            {
                ArtifactKind.FormXml or ArtifactKind.RibbonXml => ValidateXml(content),
                ArtifactKind.JavaScript => ValidateUtf8Text(content),
                ArtifactKind.PluginCatalog => ValidateJson(content, _options.ExcludeSecureConfiguration),
                ArtifactKind.EntityMetadata or ArtifactKind.CustomPageCatalog =>
                    ValidateJson(content, sanitizeSecureConfiguration: false),
                ArtifactKind.PluginAssembly => ValidateManagedAssembly(content),
                _ => throw new ApiInputException("The artifact kind is not supported.", $"{field}.kind")
            };
        }
        catch (ApiInputException)
        {
            throw;
        }
        catch (Exception exception) when (exception is DecoderFallbackException or JsonException or XmlException or BadImageFormatException)
        {
            throw new ApiInputException(
                $"The artifact content is invalid for {kind}: {exception.Message}",
                $"{field}.contentBase64");
        }
    }

    private static byte[] ValidateXml(byte[] content)
    {
        var normalized = ValidateUtf8Text(content);
        using var stream = new MemoryStream(normalized, writable: false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = normalized.LongLength,
            IgnoreComments = false,
            IgnoreWhitespace = false
        });

        while (reader.Read())
        {
        }

        return normalized;
    }

    private static byte[] ValidateJson(byte[] content, bool sanitizeSecureConfiguration)
    {
        var normalized = ValidateUtf8Text(content);
        using var document = JsonDocument.Parse(normalized, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64
        });

        if (!sanitizeSecureConfiguration)
        {
            return normalized;
        }

        using var output = new MemoryStream(normalized.Length);
        using (var writer = new Utf8JsonWriter(output))
        {
            WriteSanitizedJson(writer, document.RootElement);
        }

        return output.ToArray();
    }

    private static void WriteSanitizedJson(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (IsSecureConfigurationEntry(element))
                {
                    writer.WriteStartObject();
                    writer.WriteBoolean("redacted", true);
                    writer.WriteEndObject();
                    return;
                }

                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    if (IsSecureConfigurationName(property.Name))
                    {
                        continue;
                    }

                    writer.WritePropertyName(property.Name);
                    WriteSanitizedJson(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteSanitizedJson(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static bool IsSecureConfigurationEntry(JsonElement element)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String &&
                NormalizePropertyName(property.Name) is "name" or "key" &&
                IsSecureConfigurationName(property.Value.GetString() ?? string.Empty))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSecureConfigurationName(string value) =>
        NormalizePropertyName(value).StartsWith("secureconfig", StringComparison.Ordinal);

    private static string NormalizePropertyName(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static byte[] ValidateManagedAssembly(byte[] content)
    {
        if (content.Length < 2 || content[0] != (byte)'M' || content[1] != (byte)'Z')
        {
            throw new BadImageFormatException("The payload is not a portable executable.");
        }

        using var stream = new MemoryStream(content, writable: false);
        using var peReader = new PEReader(stream, PEStreamOptions.LeaveOpen);
        if (!peReader.HasMetadata || peReader.PEHeaders.CorHeader is null)
        {
            throw new BadImageFormatException("The payload is not a managed .NET assembly.");
        }

        return content;
    }

    private static byte[] ValidateUtf8Text(byte[] content)
    {
        var text = StrictUtf8.GetString(content);
        if (text.IndexOf('\0') >= 0)
        {
            throw new DecoderFallbackException("NUL characters are not accepted in text artifacts.");
        }

        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            text = text[1..];
        }

        return Encoding.UTF8.GetBytes(text);
    }

    private byte[] DecodeBase64(string? value, string field)
    {
        if (string.IsNullOrEmpty(value) || value.Length % 4 != 0)
        {
            throw new ApiInputException("contentBase64 must be canonical Base64.", $"{field}.contentBase64");
        }

        var padding = value.EndsWith("==", StringComparison.Ordinal) ? 2 :
            value.EndsWith('=') ? 1 : 0;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            var isAlphabet = character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/';
            var isPadding = character == '=' && index >= value.Length - padding;
            if (!isAlphabet && !isPadding)
            {
                throw new ApiInputException("contentBase64 must be canonical Base64.", $"{field}.contentBase64");
            }
        }

        var decodedLength = checked((value.Length / 4L * 3L) - padding);
        if (decodedLength > _options.MaxArtifactBytes)
        {
            throw new ApiInputException(
                $"An artifact cannot exceed {_options.MaxArtifactBytes} decoded bytes.",
                $"{field}.contentBase64",
                StatusCodes.Status413PayloadTooLarge);
        }

        var content = GC.AllocateUninitializedArray<byte>(checked((int)decodedLength));
        if (!Convert.TryFromBase64String(value, content, out var bytesWritten) || bytesWritten != content.Length)
        {
            throw new ApiInputException("contentBase64 is invalid.", $"{field}.contentBase64");
        }

        if (!string.Equals(Convert.ToBase64String(content), value, StringComparison.Ordinal))
        {
            throw new ApiInputException("contentBase64 must use canonical Base64 encoding.", $"{field}.contentBase64");
        }

        return content;
    }

    private static string? ValidateSourceUrl(string? value, string organizationUrl, string field)
    {
        if (value is null)
        {
            return null;
        }

        ValidateText(value, 1, 2048, $"{field}.sourceUrl");
        var organizationUri = ParseOrganizationUri(organizationUrl);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var sourceUri) ||
            sourceUri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(sourceUri.UserInfo) ||
            !string.IsNullOrEmpty(sourceUri.Fragment))
        {
            throw new ApiInputException("sourceUrl must be an absolute HTTP(S) URL without credentials or a fragment.", $"{field}.sourceUrl");
        }

        if (!string.Equals(sourceUri.Scheme, organizationUri.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(sourceUri.IdnHost, organizationUri.IdnHost, StringComparison.OrdinalIgnoreCase) ||
            sourceUri.Port != organizationUri.Port)
        {
            throw new ApiInputException("sourceUrl must use the same origin as context.organizationUrl.", $"{field}.sourceUrl");
        }

        return sourceUri.AbsoluteUri;
    }

    private static Uri ParseOrganizationUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ApiInputException(
                "organizationUrl must be an absolute HTTP(S) URL without credentials, a query, or a fragment.",
                "context.organizationUrl");
        }

        return uri;
    }

    private static void ValidateOptionalGuid(string? value, string field)
    {
        if (value is not null && !Guid.TryParse(value.Trim('{', '}'), out _))
        {
            throw new ApiInputException("The value must be a GUID.", field);
        }
    }

    private static void ValidateOptionalText(string? value, int maxLength, string field)
    {
        if (value is not null)
        {
            ValidateText(value, 1, maxLength, field);
        }
    }

    private static void ValidateText(string? value, int minLength, int maxLength, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < minLength || value.Length > maxLength ||
            value.Any(char.IsControl))
        {
            throw new ApiInputException($"The value must contain between {minLength} and {maxLength} valid characters.", field);
        }
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex LogicalNamePattern();
}
