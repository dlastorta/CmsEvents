namespace CmsEvents.Api;

using System.Globalization;
using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.Exporter;
using CmsEvents.Api.Configuration;
using CmsEvents.Api.Endpoints;
using CmsEvents.Api.Middleware;
using CmsEvents.Application.DependencyInjection;
using CmsEvents.Infrastructure.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;

public class Program
{
    // Private constructor prevents instantiation; the class exists only as a container for the
    // Main entry point and as a type anchor for WebApplicationFactory<Program> in integration tests.
    private Program() { }

    public static async Task<int> Main(string[] args)
    {
        // Bootstrap logger — used only until the host is built and Serilog config is fully loaded.
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
            .CreateBootstrapLogger();

        try
        {
            var builder = WebApplication.CreateBuilder(args);

            ConfigureConfiguration(builder);
            ConfigureSerilog(builder);
            ConfigureServices(builder);

            var app = builder.Build();

            ConfigurePipeline(app);

            await app.RunAsync();
            return 0;
        }
        catch (Exception ex) when (ex is not HostAbortedException)
        {
            Log.Fatal(ex, "Host terminated unexpectedly.");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    private static void ConfigureConfiguration(WebApplicationBuilder builder)
    {
        // Order: appsettings.json -> appsettings.{Env}.json -> User Secrets (dev) -> env vars -> Key Vault (prod).
        if (builder.Environment.IsDevelopment())
        {
            builder.Configuration.AddUserSecrets<Program>(optional: true);
        }

        builder.Configuration.AddEnvironmentVariables();

        var keyVaultUri = builder.Configuration["KeyVaultUri"];
        if (!string.IsNullOrWhiteSpace(keyVaultUri))
        {
            builder.Configuration.AddAzureKeyVault(
                new Uri(keyVaultUri),
                new DefaultAzureCredential(),
                new KeyVaultSecretManager());
        }
    }

    private static void ConfigureSerilog(WebApplicationBuilder builder)
    {
        builder.Host.UseSerilog((context, services, config) => config
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .WriteTo.Console(
                outputTemplate:
                    "[{Timestamp:HH:mm:ss} {Level:u3}] {CorrelationId} {Message:lj}{NewLine}{Exception}",
                formatProvider: CultureInfo.InvariantCulture)
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning));
    }

    private static void ConfigureServices(WebApplicationBuilder builder)
    {
        var services = builder.Services;
        var configuration = builder.Configuration;

        // Layers.
        services.AddApplication();
        services.AddInfrastructure(configuration);

        // Authorization policies per ADR-011.
        services.AddAuthorization(AuthorizationPolicies.Configure);

        // Processing timeout per ADR-009 — bind via Get<T>() so the options class is instantiated
        // (making its property init logic testable and observable in coverage). Configure<T>() also
        // registers it for IOptions<T> consumers if any emerge.
        var processingOptions = configuration.GetSection(ProcessingOptions.SectionName).Get<ProcessingOptions>()
            ?? new ProcessingOptions();
        services.Configure<ProcessingOptions>(configuration.GetSection(ProcessingOptions.SectionName));
        services.AddRequestTimeouts(options =>
        {
            options.AddPolicy(
                RequestTimeoutPolicies.EventBatch,
                TimeSpan.FromSeconds(processingOptions.TimeoutSeconds));
        });

        // Rate limiting per ADR-013 — extracted to RateLimitingSetup for method-complexity budget.
        services.AddRateLimitingPolicies(configuration);

        // Observability per ADR-014.
        AddOpenTelemetry(services, configuration);

        // API surface.
        services.AddEndpointsApiExplorer();
        services.AddOpenApi();
        services.AddProblemDetails();
    }

    private static void AddOpenTelemetry(IServiceCollection services, IConfiguration configuration)
    {
        var applicationInsightsConnectionString =
            configuration["Observability:ApplicationInsightsConnectionString"];

        services.AddOpenTelemetry()
            .WithTracing(tracing =>
            {
                tracing
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();

                if (!string.IsNullOrWhiteSpace(applicationInsightsConnectionString))
                {
                    tracing.AddAzureMonitorTraceExporter(o =>
                        o.ConnectionString = applicationInsightsConnectionString);
                }
                else
                {
                    tracing.AddConsoleExporter();
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();

                if (!string.IsNullOrWhiteSpace(applicationInsightsConnectionString))
                {
                    metrics.AddAzureMonitorMetricExporter(o =>
                        o.ConnectionString = applicationInsightsConnectionString);
                }
            });
    }

    private static void ConfigurePipeline(WebApplication app)
    {
        app.UseSerilogRequestLogging();
        app.UseMiddleware<CorrelationIdMiddleware>();

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.UseRequestTimeouts();

        app.MapCmsEventsEndpoints();
        app.MapEntitiesEndpoints();
    }

}
