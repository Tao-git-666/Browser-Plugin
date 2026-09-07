using System.Text.RegularExpressions;

namespace CrmLogicLens.Api.Services;

public sealed record DiagnosticSkill(
    string Id,
    string Description,
    string Version,
    IReadOnlyList<string> Triggers,
    IReadOnlyList<string> RequiredTools,
    IReadOnlyList<string> RequiredWhenAvailableTools,
    IReadOnlyList<string> RequiredSignals,
    string Instructions,
    bool IsFallback,
    string SourcePath);

public sealed record DiagnosticSkillMatch(DiagnosticSkill Skill, int Score);

/// <summary>
/// Loads server-owned, instruction-only diagnostic skills. Skill files may select
/// allow-listed read-only tools, but cannot register code or execute scripts.
/// </summary>
public sealed partial class DiagnosticSkillCatalog
{
    private const int MaxSkillFiles = 64;
    private const int MaxSkillCharacters = 32_000;
    private static readonly HashSet<string> AllowedRequiredTools = new(StringComparer.Ordinal)
    {
        "find_business_logic",
        "trace_evidence",
        "list_current_entity_plugin_steps",
        "read_javascript_function",
        "resolve_custom_api",
        "read_custom_api_implementation",
        "read_recorded_runtime_events",
        "read_recorded_dataverse_queries",
        "read_runtime_errors",
        "read_decompiled_plugin",
        "read_current_form_values",
        "query_crm_data",
        "search_environment_code", "read_environment_code"
    };
    private static readonly HashSet<string> AllowedSignals = new(StringComparer.Ordinal)
    {
        "runtime-recording",
        "recorded-dataverse-queries",
        "runtime-errors",
        "crm-data-access",
        "environment-code-library"
    };
    private readonly IReadOnlyDictionary<string, DiagnosticSkill> _skills;

    public DiagnosticSkillCatalog(IEnumerable<DiagnosticSkill> skills)
    {
        var materialized = skills.ToArray();
        if (materialized.Length > MaxSkillFiles)
        {
            throw new InvalidOperationException($"Diagnostic skill count cannot exceed {MaxSkillFiles}.");
        }
        _skills = materialized.ToDictionary(skill => skill.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<DiagnosticSkill> Skills => _skills.Values
        .OrderBy(skill => skill.Id, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public static DiagnosticSkillCatalog LoadFromDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var root = Path.GetFullPath(directory);
        if (!Directory.Exists(root))
        {
            throw new InvalidOperationException($"Diagnostic skill directory does not exist: {root}");
        }

        var files = Directory.GetFiles(root, "SKILL.md", SearchOption.AllDirectories);
        if (files.Length is 0 or > MaxSkillFiles)
        {
            throw new InvalidOperationException(
                $"Diagnostic skill directory must contain between 1 and {MaxSkillFiles} SKILL.md files.");
        }

        var skills = new List<DiagnosticSkill>(files.Length);
        foreach (var file in files.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            var fullPath = Path.GetFullPath(file);
            if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Diagnostic skill path escaped the configured directory.");
            }
            var content = File.ReadAllText(fullPath);
            if (content.Length is 0 or > MaxSkillCharacters || content.IndexOf('\0') >= 0)
            {
                throw new InvalidOperationException($"Diagnostic skill has invalid size or content: {fullPath}");
            }
            skills.Add(Parse(fullPath, content));
        }
        return new DiagnosticSkillCatalog(skills);
    }

    public DiagnosticSkill? Find(string id) =>
        _skills.GetValueOrDefault((id ?? string.Empty).Trim());

    public IReadOnlyList<DiagnosticSkillMatch> Search(
        string query,
        int limit = 3,
        IReadOnlySet<string>? availableSignals = null)
    {
        var normalized = (query ?? string.Empty).Trim();
        if (normalized.Length is 0 or > 2_000)
        {
            return [];
        }
        limit = Math.Clamp(limit, 1, 5);
        var terms = SearchTerms().Split(normalized.ToLowerInvariant())
            .Where(term => term.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(24)
            .ToArray();

        var ranked = _skills.Values
            .Where(skill => IsApplicable(skill, availableSignals))
            .Select(skill => new DiagnosticSkillMatch(skill, Score(skill, normalized, terms)))
            .Where(match => match.Score > 0)
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Skill.IsFallback)
            .ThenBy(match => match.Skill.Id, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToArray();
        if (ranked.Length > 0)
        {
            return ranked;
        }

        var fallback = _skills.Values.FirstOrDefault(skill =>
            skill.IsFallback && IsApplicable(skill, availableSignals));
        return fallback is null ? [] : [new DiagnosticSkillMatch(fallback, 1)];
    }

    public static bool HasAmbiguousTopMatch(IReadOnlyList<DiagnosticSkillMatch> matches)
    {
        var candidates = matches
            .Where(match => !match.Skill.IsFallback)
            .Take(2)
            .ToArray();
        return candidates.Length == 2 && candidates[1].Score * 5 >= candidates[0].Score * 4;
    }

    private static bool IsApplicable(DiagnosticSkill skill, IReadOnlySet<string>? availableSignals) =>
        skill.RequiredSignals.Count == 0 ||
        availableSignals is not null && skill.RequiredSignals.All(availableSignals.Contains);

    private static int Score(DiagnosticSkill skill, string query, IReadOnlyList<string> terms)
    {
        var triggerScore = 0;
        foreach (var trigger in skill.Triggers)
        {
            if (query.Contains(trigger, StringComparison.OrdinalIgnoreCase))
            {
                triggerScore = Math.Max(triggerScore, 30 + Math.Min(trigger.Length, 20));
                continue;
            }
            var triggerBigrams = CjkBigrams(trigger);
            if (triggerBigrams.Length > 0)
            {
                var shared = triggerBigrams.Count(query.Contains);
                if (shared >= Math.Max(2, (triggerBigrams.Length + 1) / 2))
                {
                    triggerScore = Math.Max(triggerScore, 12 + shared);
                }
            }
        }

        if (triggerScore == 0)
        {
            return skill.IsFallback ? 1 : 0;
        }

        var score = triggerScore;
        var searchable = $"{skill.Id} {skill.Description}";
        foreach (var term in terms)
        {
            if (searchable.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 3;
            }
        }
        return score;
    }

    private static string[] CjkBigrams(string value)
    {
        var cjk = new string(value.Where(character => character is >= '\u3400' and <= '\u9fff').ToArray());
        return cjk.Length < 2
            ? []
            : Enumerable.Range(0, cjk.Length - 1)
                .Select(index => cjk.Substring(index, 2))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
    }

    private static DiagnosticSkill Parse(string path, string content)
    {
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        if (lines.Length < 4 || !string.Equals(lines[0].Trim(), "---", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Diagnostic skill is missing YAML frontmatter: {path}");
        }
        var end = Array.FindIndex(lines, 1, line => string.Equals(line.Trim(), "---", StringComparison.Ordinal));
        if (end < 2)
        {
            throw new InvalidOperationException($"Diagnostic skill frontmatter is not closed: {path}");
        }

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 1; index < end; index++)
        {
            var line = lines[index];
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                throw new InvalidOperationException($"Diagnostic skill has invalid frontmatter: {path}");
            }
            metadata[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        var id = Require(metadata, "name", path);
        var directoryName = new DirectoryInfo(Path.GetDirectoryName(path)!).Name;
        if (!SkillNamePattern().IsMatch(id) || !string.Equals(id, directoryName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Diagnostic skill name must match its folder and use lowercase kebab-case: {path}");
        }
        var description = Require(metadata, "description", path);
        if (description.Length > 500)
        {
            throw new InvalidOperationException($"Diagnostic skill description is too long: {path}");
        }
        var version = metadata.GetValueOrDefault("version", "1.0");
        if (version.Length > 32)
        {
            throw new InvalidOperationException($"Diagnostic skill version is too long: {path}");
        }
        var triggers = SplitList(metadata.GetValueOrDefault("triggers"), 32, 80, path, "triggers");
        var required = SplitTools(metadata.GetValueOrDefault("required-tools"), path);
        var requiredWhenAvailable = SplitTools(metadata.GetValueOrDefault("required-when-available"), path);
        var requiredSignals = SplitSignals(metadata.GetValueOrDefault("requires-signals"), path);
        var instructions = string.Join('\n', lines[(end + 1)..]).Trim();
        if (instructions.Length is 0 or > 24_000)
        {
            throw new InvalidOperationException($"Diagnostic skill instructions are empty or too long: {path}");
        }
        var fallback = bool.TryParse(metadata.GetValueOrDefault("fallback"), out var parsedFallback) && parsedFallback;
        return new DiagnosticSkill(
            id,
            description,
            version,
            triggers,
            required,
            requiredWhenAvailable,
            requiredSignals,
            instructions,
            fallback,
            path);
    }

    private static string Require(IReadOnlyDictionary<string, string> metadata, string name, string path)
    {
        var value = metadata.GetValueOrDefault(name)?.Trim();
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Diagnostic skill is missing {name}: {path}")
            : value;
    }

    private static IReadOnlyList<string> SplitTools(string? value, string path)
    {
        var tools = SplitList(value, 16, 80, path, "tool list");
        if (tools.Any(tool => !AllowedRequiredTools.Contains(tool)))
        {
            throw new InvalidOperationException($"Diagnostic skill references an unknown or non-allow-listed tool: {path}");
        }
        return tools;
    }

    private static IReadOnlyList<string> SplitSignals(string? value, string path)
    {
        var signals = SplitList(value, 8, 80, path, "signal list");
        if (signals.Any(signal => !AllowedSignals.Contains(signal)))
        {
            throw new InvalidOperationException($"Diagnostic skill references an unknown signal: {path}");
        }
        return signals;
    }

    private static IReadOnlyList<string> SplitList(
        string? value,
        int maxItems,
        int maxItemLength,
        string path,
        string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }
        var items = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (items.Length > maxItems || items.Any(item => item.Length > maxItemLength))
        {
            throw new InvalidOperationException($"Diagnostic skill {field} exceeds its limit: {path}");
        }
        return items;
    }

    [GeneratedRegex(@"^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SkillNamePattern();

    [GeneratedRegex(@"[^\p{L}\p{N}_]+", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SearchTerms();
}
