namespace CrmLogicLens.Api.Configuration;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string DataDirectory { get; set; } = "App_Data";

    public long MaxArtifactBytes { get; set; } = 25 * 1024 * 1024;

    public long MaxSnapshotBytes { get; set; } = 64 * 1024 * 1024;

    public long MaxRequestBodyBytes { get; set; } = 96 * 1024 * 1024;

    public int MaxArtifactsPerSnapshot { get; set; } = 128;

    public bool ExcludeSecureConfiguration { get; set; } = true;
}

public sealed class AnalysisQueueOptions
{
    public const string SectionName = "AnalysisQueue";

    public int Capacity { get; set; } = 32;
}

public sealed class DecompilerOptions
{
    public const string SectionName = "Decompiler";

    public string? WorkerPath { get; set; }

    public string TempDirectory { get; set; } = "DecompilerTemp";

    public int TimeoutSeconds { get; set; } = 60;

    public long MaxInputBytes { get; set; } = 25 * 1024 * 1024;

    public long MaxOutputBytes { get; set; } = 8 * 1024 * 1024;

    public int MaxAssembliesPerSnapshot { get; set; } = 8;

    public int MaxDiagnosticCharacters { get; set; } = 4_096;

    public bool EagerAnalysis { get; set; } = false;
}

public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    public bool EnableWindowsAuthentication { get; set; } = true;

    public string[] CorsAllowedOrigins { get; set; } = [];

    public RateLimitOptions RateLimit { get; set; } = new();
}

public sealed class RateLimitOptions
{
    public int PermitLimit { get; set; } = 60;

    public int WindowSeconds { get; set; } = 60;

    public int QueueLimit { get; set; } = 4;
}
