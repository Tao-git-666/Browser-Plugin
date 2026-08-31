using System.Security.Cryptography;
using System.Text;

namespace CrmLogicLens.Core;

internal sealed class EvidenceGraphBuilder
{
    private readonly Dictionary<string, EvidenceNode> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EvidenceEdge> _edges = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _warnings = [];

    public IEnumerable<EvidenceNode> Nodes => _nodes.Values;
    public IEnumerable<EvidenceEdge> Edges => _edges.Values;

    public void AddNode(EvidenceNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (string.IsNullOrWhiteSpace(node.Id))
        {
            return;
        }

        if (!_nodes.TryGetValue(node.Id, out var existing))
        {
            _nodes[node.Id] = node;
            return;
        }

        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (existing.Properties is not null)
        {
            foreach (var pair in existing.Properties)
            {
                properties[pair.Key] = pair.Value;
            }
        }

        if (node.Properties is not null)
        {
            foreach (var pair in node.Properties)
            {
                properties[pair.Key] = pair.Value;
            }
        }

        _nodes[node.Id] = existing with
        {
            Label = Prefer(existing.Label, node.Label),
            Summary = Prefer(existing.Summary, node.Summary),
            ArtifactName = Prefer(existing.ArtifactName, node.ArtifactName),
            Location = existing.Location ?? node.Location,
            Confidence = Stronger(existing.Confidence, node.Confidence),
            Properties = properties.Count == 0 ? null : properties
        };
    }

    public void AddEdge(EvidenceEdge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        if (string.IsNullOrWhiteSpace(edge.SourceId) || string.IsNullOrWhiteSpace(edge.TargetId))
        {
            return;
        }

        var key = $"{edge.SourceId}\u001f{edge.TargetId}\u001f{edge.Relation}";
        if (_edges.TryGetValue(key, out var existing))
        {
            _edges[key] = existing with { Confidence = Stronger(existing.Confidence, edge.Confidence) };
        }
        else
        {
            _edges[key] = edge;
        }
    }

    public void AddWarning(string? warning)
    {
        if (!string.IsNullOrWhiteSpace(warning) &&
            !_warnings.Contains(warning, StringComparer.Ordinal))
        {
            _warnings.Add(warning);
        }
    }

    public void Merge(EvidenceGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        foreach (var node in graph.Nodes)
        {
            AddNode(node);
        }

        foreach (var edge in graph.Edges)
        {
            AddEdge(edge);
        }

        foreach (var warning in graph.Warnings)
        {
            AddWarning(warning);
        }
    }

    public EvidenceGraph Build() => new(
        _nodes.Values.OrderBy(node => node.Kind, StringComparer.Ordinal)
            .ThenBy(node => node.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(node => node.Id, StringComparer.Ordinal)
            .ToArray(),
        _edges.Values.OrderBy(edge => edge.SourceId, StringComparer.Ordinal)
            .ThenBy(edge => edge.Relation, StringComparer.Ordinal)
            .ThenBy(edge => edge.TargetId, StringComparer.Ordinal)
            .ToArray(),
        _warnings.ToArray());

    private static string Prefer(string first, string second) =>
        string.IsNullOrWhiteSpace(first) || second.Length > first.Length ? second : first;

    private static EvidenceConfidence Stronger(EvidenceConfidence first, EvidenceConfidence second) =>
        Rank(first) >= Rank(second) ? first : second;

    private static int Rank(EvidenceConfidence value) => value switch
    {
        EvidenceConfidence.Confirmed => 3,
        EvidenceConfidence.Inferred => 2,
        _ => 1
    };
}

internal static class EvidenceId
{
    public static string Create(string kind, params string?[] parts)
    {
        var canonical = string.Join("\u001f", parts.Select(part => part?.Trim().ToLowerInvariant() ?? string.Empty));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return $"{NormalizePrefix(kind)}:{Convert.ToHexString(hash.AsSpan(0, 10)).ToLowerInvariant()}";
    }

    public static string NormalizeExternal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Trim().Trim('{', '}').ToLowerInvariant();
    }

    private static string NormalizePrefix(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            {
                builder.Append(character);
            }
        }

        return builder.Length == 0 ? "evidence" : builder.ToString();
    }
}

internal static class EvidenceProperties
{
    public static IReadOnlyDictionary<string, string>? Create(params (string Key, object? Value)[] values)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in values)
        {
            var text = value?.ToString();
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(text))
            {
                result[key] = text;
            }
        }

        return result.Count == 0 ? null : result;
    }
}
