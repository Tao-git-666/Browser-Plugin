namespace CrmLogicLens.Api.Configuration;

public sealed class AiModelOptions
{
    public const string SectionName = "AI";

    public bool Enabled { get; set; } = false;

    public string Provider { get; set; } = "DeepSeek";

    public string BaseUrl { get; set; } = "https://api.deepseek.com";

    public string Model { get; set; } = "deepseek-v4-pro";

    public string ApiKey { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 900;

    public int MaxContextTokens { get; set; } = 262_144;

    public int MaxCompletionTokens { get; set; } = 8_192;

    public int MaxInvestigationCompletionTokens { get; set; } = 4_096;

    public int MaxEvidenceCharacters { get; set; } = 180_000;

    public int MaxInvestigationSeconds { get; set; } = 900;

    public int MaxToolResultCharacters { get; set; } = 160_000;
}
