using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using CrmLogicLens.Api.Configuration;
using CrmLogicLens.Api.Errors;
using CrmLogicLens.Api.Features;
using CrmLogicLens.Api.Health;
using CrmLogicLens.Api.Services;
using CrmLogicLens.Api.Storage;
using CrmLogicLens.Api.Validation;
using CrmLogicLens.Core;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Server.IIS;
using Microsoft.Extensions.Options;

namespace CrmLogicLens.Api;

public static class ApiHostExtensions
{
    public const string CorsPolicy = "edge-extension";
    public const string ApiRateLimitPolicy = "api";

    public static WebApplicationBuilder AddCrmLogicLensApi(this WebApplicationBuilder builder)
    {
        var storageConfiguration = builder.Configuration
            .GetSection(StorageOptions.SectionName)
            .Get<StorageOptions>() ?? new StorageOptions();
        var securityConfiguration = builder.Configuration
            .GetSection(SecurityOptions.SectionName)
            .Get<SecurityOptions>() ?? new SecurityOptions();
        securityConfiguration.CorsAllowedOrigins ??= [];
        securityConfiguration.RateLimit ??= new RateLimitOptions();

        builder.Services.AddOptions<StorageOptions>()
            .Bind(builder.Configuration.GetSection(StorageOptions.SectionName))
            .Validate(ValidateStorageOptions, "Storage limits or DataDirectory are invalid.")
            .ValidateOnStart();
        builder.Services.AddOptions<AnalysisQueueOptions>()
            .Bind(builder.Configuration.GetSection(AnalysisQueueOptions.SectionName))
            .Validate(options => options.Capacity is >= 1 and <= 10_000, "Capacity must be between 1 and 10000.")
            .ValidateOnStart();
        builder.Services.AddOptions<DecompilerOptions>()
            .Bind(builder.Configuration.GetSection(DecompilerOptions.SectionName))
            .Validate(ValidateDecompilerOptions, "Decompiler paths or resource limits are invalid.")
            .ValidateOnStart();
        builder.Services.AddOptions<AiModelOptions>()
            .Bind(builder.Configuration.GetSection(AiModelOptions.SectionName))
            .Validate(ValidateAiModelOptions, "AI model configuration is invalid.")
            .ValidateOnStart();
        builder.Services.AddOptions<SecurityOptions>()
            .Bind(builder.Configuration.GetSection(SecurityOptions.SectionName))
            .Validate(ValidateSecurityOptions, "Security, CORS, or rate-limit settings are invalid.")
            .ValidateOnStart();

        builder.WebHost.ConfigureKestrel(options =>
            options.Limits.MaxRequestBodySize = storageConfiguration.MaxRequestBodyBytes);
        builder.Services.Configure<IISServerOptions>(options =>
            options.MaxRequestBodySize = storageConfiguration.MaxRequestBodyBytes);

        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<ApiExceptionHandler>();
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.SerializerOptions.PropertyNameCaseInsensitive = true;
            options.SerializerOptions.Converters.Add(
                new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        });

        builder.Services.AddCors(options => options.AddPolicy(CorsPolicy, policy =>
        {
            if (securityConfiguration.CorsAllowedOrigins.Length > 0)
            {
                policy.WithOrigins(securityConfiguration.CorsAllowedOrigins)
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    .AllowCredentials();
            }
        }));

        var requireWindowsAuthentication =
            !builder.Environment.IsDevelopment() && securityConfiguration.EnableWindowsAuthentication;
        if (requireWindowsAuthentication)
        {
            builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme)
                .AddNegotiate();
            builder.Services.AddAuthorization();
        }

        AddRateLimiting(builder.Services, securityConfiguration.RateLimit);

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IAnalysisStore, FileAnalysisStore>();
        builder.Services.AddSingleton<SnapshotUploadValidator>();
        builder.Services.AddSingleton<AnalysisJobQueue>();
        builder.Services.AddSingleton<SnapshotIngestionService>();
        builder.Services.AddSingleton<DecompilerProcessService>();
        builder.Services.AddSingleton<DecompiledArtifactService>();
        builder.Services.AddSingleton(services =>
        {
            var environment = services.GetRequiredService<IWebHostEnvironment>();
            return DiagnosticSkillCatalog.LoadFromDirectory(
                Path.Combine(environment.ContentRootPath, "DiagnosticSkills"));
        });
        builder.Services.AddSingleton(CreateAnalysisPipeline);
        builder.Services.AddSingleton<EvidenceAnswerService>();
        builder.Services.AddSingleton<AiInvestigationContinuationStore>();
        builder.Services.AddHttpClient<OpenAiCompatibleChatClient>((services, client) =>
        {
            var options = services.GetRequiredService<IOptions<AiModelOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        });
        builder.Services.AddScoped<EvidenceQueryService>();
        builder.Services.AddHostedService<AnalysisWorker>();
        builder.Services.AddHealthChecks().AddCheck<StorageHealthCheck>("storage", tags: ["ready"]);

        return builder;
    }

    public static WebApplication UseCrmLogicLensApi(this WebApplication app)
    {
        var security = app.Services.GetRequiredService<IOptions<SecurityOptions>>().Value;
        var requireWindowsAuthentication =
            !app.Environment.IsDevelopment() && security.EnableWindowsAuthentication;

        app.UseExceptionHandler();
        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
            app.UseHttpsRedirection();
        }

        app.UseCors(CorsPolicy);
        if (requireWindowsAuthentication)
        {
            app.UseAuthentication();
            app.UseAuthorization();
        }

        app.UseRateLimiter();

        app.MapApiEndpoints(requireWindowsAuthentication);
        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            AllowCachingResponses = false
        }).AllowAnonymous();

        return app;
    }

    private static void AddRateLimiting(IServiceCollection services, RateLimitOptions configuration)
    {
        services.AddRateLimiter(options =>
        {
            options.AddPolicy(ApiRateLimitPolicy, httpContext =>
            {
                var partitionKey = httpContext.User.Identity?.Name
                    ?? httpContext.Connection.RemoteIpAddress?.ToString()
                    ?? "unknown";

                return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = configuration.PermitLimit,
                    Window = TimeSpan.FromSeconds(configuration.WindowSeconds),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = configuration.QueueLimit,
                    AutoReplenishment = true
                });
            });

            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                var problemDetails = context.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
                await problemDetails.TryWriteAsync(new ProblemDetailsContext
                {
                    HttpContext = context.HttpContext,
                    ProblemDetails = new Microsoft.AspNetCore.Mvc.ProblemDetails
                    {
                        Status = StatusCodes.Status429TooManyRequests,
                        Title = "Too many requests",
                        Detail = "The API rate limit was exceeded. Try again later.",
                        Instance = context.HttpContext.Request.Path
                    }
                });
            };
        });
    }

    private static bool ValidateStorageOptions(StorageOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.DataDirectory) ||
            options.MaxArtifactBytes is < 1 or > 512L * 1024 * 1024 ||
            options.MaxSnapshotBytes < options.MaxArtifactBytes ||
            options.MaxSnapshotBytes > 1024L * 1024 * 1024 ||
            options.MaxRequestBodyBytes > 1536L * 1024 * 1024 ||
            options.MaxArtifactsPerSnapshot is < 1 or > 10_000)
        {
            return false;
        }

        var base64Bytes = checked(4L * ((options.MaxSnapshotBytes + 2) / 3));
        var minimumRequestBytes = checked(base64Bytes + (options.MaxArtifactsPerSnapshot * 2_048L) + 65_536L);
        return options.MaxRequestBodyBytes >= minimumRequestBytes;
    }

    private static bool ValidateSecurityOptions(SecurityOptions options) =>
        options.RateLimit is not null &&
        options.CorsAllowedOrigins is not null &&
        options.RateLimit.PermitLimit is >= 1 and <= 100_000 &&
        options.RateLimit.WindowSeconds is >= 1 and <= 86_400 &&
        options.RateLimit.QueueLimit is >= 0 and <= 10_000 &&
        options.CorsAllowedOrigins.All(IsValidCorsOrigin);

    private static bool ValidateDecompilerOptions(DecompilerOptions options) =>
        !string.IsNullOrWhiteSpace(options.TempDirectory) &&
        options.TimeoutSeconds is >= 1 and <= 600 &&
        options.MaxInputBytes is >= 1 and <= 64L * 1024 * 1024 &&
        options.MaxOutputBytes is >= 1 and <= 64L * 1024 * 1024 &&
        options.MaxAssembliesPerSnapshot is >= 1 and <= 32 &&
        options.MaxOutputBytes * options.MaxAssembliesPerSnapshot <= 256L * 1024 * 1024 &&
        options.MaxDiagnosticCharacters is >= 128 and <= 65_536;

    private static bool ValidateAiModelOptions(AiModelOptions options) =>
        Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri) &&
        baseUri.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(baseUri.UserInfo) &&
        string.IsNullOrEmpty(baseUri.Query) &&
        string.IsNullOrEmpty(baseUri.Fragment) &&
        !string.IsNullOrWhiteSpace(options.Provider) &&
        options.Provider.Length <= 64 &&
        !string.IsNullOrWhiteSpace(options.Model) &&
        options.Model.Length <= 128 &&
        options.ApiKey.Length <= 4_096 &&
        options.TimeoutSeconds is >= 5 and <= 900 &&
        options.MaxContextTokens is >= 16_384 and <= 1_048_576 &&
        options.MaxCompletionTokens is >= 128 and <= 32_768 &&
        options.MaxInvestigationCompletionTokens is >= 128 and <= 8_192 &&
        options.MaxCompletionTokens + 8_192 < options.MaxContextTokens &&
        options.MaxEvidenceCharacters is >= 4_096 and <= 500_000 &&
        options.MaxInvestigationSeconds is >= 60 and <= 3_600 &&
        options.MaxToolResultCharacters >= 4_096 &&
        options.MaxToolResultCharacters <= options.MaxEvidenceCharacters;

    private static AnalysisPipeline CreateAnalysisPipeline(IServiceProvider services)
    {
        var storage = services.GetRequiredService<IOptions<StorageOptions>>().Value;
        var decompiler = services.GetRequiredService<IOptions<DecompilerOptions>>().Value;
        var decoder = new ArtifactDecoder(new ArtifactDecoderOptions
        {
            MaxTextArtifactBytes = checked((int)Math.Max(storage.MaxArtifactBytes, decompiler.MaxOutputBytes)),
            MaxPluginAssemblyBytes = checked((int)storage.MaxArtifactBytes),
            MaxArtifactNameLength = 512,
            MaxEncodedWhitespace = 0,
            MaxArtifactsPerSnapshot = checked(storage.MaxArtifactsPerSnapshot + decompiler.MaxAssembliesPerSnapshot),
            MaxSnapshotBytes = checked(
                storage.MaxSnapshotBytes + (decompiler.MaxOutputBytes * decompiler.MaxAssembliesPerSnapshot))
        });

        return new AnalysisPipeline(
            decoder,
            new FormXmlAnalyzer(),
            new RibbonXmlAnalyzer(),
            new JavaScriptAnalyzer(),
            new PluginCatalogAnalyzer(),
            new PluginAssemblyInspector(),
            new EntityMetadataAnalyzer(),
            new CSharpPluginAnalyzer());
    }

    private static bool IsValidCorsOrigin(string origin)
    {
        if (string.IsNullOrWhiteSpace(origin) || origin.Contains('*') || origin.EndsWith('/'))
        {
            return false;
        }

        return Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https" or "chrome-extension" or "ms-browser-extension" &&
            string.IsNullOrEmpty(uri.Query) &&
            string.IsNullOrEmpty(uri.Fragment) &&
            string.IsNullOrEmpty(uri.UserInfo);
    }
}
