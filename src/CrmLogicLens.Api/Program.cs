using CrmLogicLens.Api;

var builder = WebApplication.CreateBuilder(args);

// Local machine configuration is intentionally optional and excluded from source control.
// Re-adding environment variables and command-line arguments preserves their higher priority.
builder.Configuration
    .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables();
if (args.Length > 0)
{
    builder.Configuration.AddCommandLine(args);
}

builder.AddCrmLogicLensApi();

var app = builder.Build();

app.UseCrmLogicLensApi();

app.Run();

public partial class Program;
