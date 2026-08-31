using System.Text;

namespace CrmLogicLens.Core;

public sealed record ArtifactDecoderOptions
{
    public int MaxTextArtifactBytes { get; init; } = 8 * 1024 * 1024;
    public int MaxPluginAssemblyBytes { get; init; } = 64 * 1024 * 1024;
    public int MaxArtifactNameLength { get; init; } = 512;
    public int MaxEncodedWhitespace { get; init; } = 16 * 1024;
    public int MaxArtifactsPerSnapshot { get; init; } = 256;
    public long MaxSnapshotBytes { get; init; } = 128L * 1024 * 1024;
}

public sealed class ArtifactValidationException : Exception
{
    public ArtifactValidationException(string message) : base(message)
    {
    }

    public ArtifactValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Performs bounded Base64 decoding. It never decompresses or executes uploaded data.
/// </summary>
public sealed class ArtifactDecoder
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ArtifactDecoderOptions _options;

    public ArtifactDecoder() : this(new ArtifactDecoderOptions())
    {
    }

    public ArtifactDecoder(ArtifactDecoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxTextArtifactBytes <= 0 || options.MaxPluginAssemblyBytes <= 0 ||
            options.MaxArtifactNameLength <= 0 || options.MaxEncodedWhitespace < 0 ||
            options.MaxArtifactsPerSnapshot <= 0 || options.MaxSnapshotBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Artifact limits must be positive.");
        }

        _options = options;
    }

    public ArtifactDecoderOptions Options => _options;

    public DecodedArtifact Decode(ArtifactUpload upload)
    {
        ArgumentNullException.ThrowIfNull(upload);
        ValidateMetadata(upload);

        var maxBytes = upload.Kind == ArtifactKind.PluginAssembly
            ? _options.MaxPluginAssemblyBytes
            : _options.MaxTextArtifactBytes;

        var encoded = upload.ContentBase64;
        if (encoded is null)
        {
            throw new ArtifactValidationException("Artifact content is missing.");
        }

        var nonWhitespace = 0;
        var whitespace = 0;
        foreach (var character in encoded)
        {
            if (char.IsWhiteSpace(character))
            {
                whitespace++;
            }
            else
            {
                nonWhitespace++;
            }
        }

        if (whitespace > _options.MaxEncodedWhitespace)
        {
            throw new ArtifactValidationException("Base64 content contains excessive whitespace.");
        }

        // Check the upper bound before Convert allocates the decoded array.
        var estimatedBytes = ((long)nonWhitespace + 3L) / 4L * 3L;
        if (estimatedBytes > maxBytes + 2L)
        {
            throw new ArtifactValidationException(
                $"Artifact exceeds the {maxBytes:N0}-byte limit for {upload.Kind}.");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(encoded);
        }
        catch (FormatException exception)
        {
            throw new ArtifactValidationException("Artifact content is not valid Base64.", exception);
        }

        if (bytes.Length > maxBytes)
        {
            throw new ArtifactValidationException(
                $"Artifact exceeds the {maxBytes:N0}-byte limit for {upload.Kind}.");
        }

        if (bytes.Length == 0 && upload.Kind != ArtifactKind.PluginAssembly)
        {
            throw new ArtifactValidationException("Text artifacts cannot be empty.");
        }

        string? text = null;
        if (upload.Kind != ArtifactKind.PluginAssembly)
        {
            try
            {
                text = StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw new ArtifactValidationException("Text artifact is not valid UTF-8.", exception);
            }

            if (text.IndexOf('\0') >= 0)
            {
                throw new ArtifactValidationException("Text artifact contains a NUL character.");
            }
        }

        return new DecodedArtifact(
            upload.Kind,
            upload.Name.Trim(),
            NormalizeOptional(upload.ComponentId),
            NormalizeOptional(upload.Version),
            NormalizeMediaType(upload.MediaType),
            bytes,
            text,
            NormalizeOptional(upload.SourceUrl));
    }

    private void ValidateMetadata(ArtifactUpload upload)
    {
        if (string.IsNullOrWhiteSpace(upload.Name) || upload.Name.Length > _options.MaxArtifactNameLength)
        {
            throw new ArtifactValidationException(
                $"Artifact name must be between 1 and {_options.MaxArtifactNameLength} characters.");
        }

        if (upload.Name.Any(char.IsControl))
        {
            throw new ArtifactValidationException("Artifact name contains control characters.");
        }

        if (string.IsNullOrWhiteSpace(upload.MediaType))
        {
            throw new ArtifactValidationException("Artifact media type is required.");
        }

        var mediaType = NormalizeMediaType(upload.MediaType);
        if (!AllowedMediaTypes(upload.Kind).Contains(mediaType, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArtifactValidationException(
                $"Media type '{mediaType}' is not allowed for {upload.Kind}.");
        }
    }

    private static IReadOnlyList<string> AllowedMediaTypes(ArtifactKind kind) => kind switch
    {
        ArtifactKind.FormXml or ArtifactKind.RibbonXml =>
            ["application/xml", "text/xml", "text/plain", "application/octet-stream"],
        ArtifactKind.JavaScript =>
            ["application/javascript", "text/javascript", "application/x-javascript", "text/plain", "application/octet-stream"],
        ArtifactKind.DecompiledCSharp =>
            ["text/x-csharp", "text/plain", "application/octet-stream"],
        ArtifactKind.PluginCatalog or ArtifactKind.EntityMetadata or ArtifactKind.CustomPageCatalog =>
            ["application/json", "text/json", "text/plain", "application/octet-stream"],
        ArtifactKind.PluginAssembly =>
            ["application/octet-stream", "application/x-msdownload", "application/vnd.microsoft.portable-executable"],
        _ => ["application/octet-stream"]
    };

    private static string NormalizeMediaType(string mediaType)
    {
        var separator = mediaType.IndexOf(';');
        return (separator >= 0 ? mediaType[..separator] : mediaType).Trim().ToLowerInvariant();
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
