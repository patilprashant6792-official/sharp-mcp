using MCP.Core.Configuration;
using MCP.Core.Services;
using Microsoft.AspNetCore.RateLimiting;
using ModelContextProtocol.Server;
using NuGet.Configuration;
using NuGetExplorer.Services;
using OllamaSharp;
using StackExchange.Redis;
using System;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// Configuration
builder.Services.Configure<NuGetServiceConfig>(
    builder.Configuration.GetSection("NuGetService"));

// Add services
builder.Services.AddControllers();
builder.Services.AddHealthChecks();

// Add in-memory caching with size limit
builder.Services.AddMemoryCache();

// Register NuGetService as singleton (thread-safe with semaphore)
builder.Services.AddSingleton<INuGetSearchService, NuGetSearchService>();
builder.Services.AddSingleton<IProjectSkeletonService>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var projectConfigService = sp.GetRequiredService<IProjectConfigService>();
    return new ProjectSkeletonService(config, projectConfigService);
});
builder.Services.AddSingleton<ITomlSerializerService, TomlSerializerService>();
builder.Services.AddSingleton<IMarkdownFormatterService, MarkdownFormatterService>();


builder.Services.AddSingleton<IMethodFormatterService, MethodFormatterService>();

builder.Services.AddSingleton<IMethodCallGraphService, MethodCallGraphService>();

builder.Services.AddSingleton<ICodeSearchService, CodeSearchService>();
builder.Services.AddSingleton<ICodeSearchFormatterService, CodeSearchFormatterService>();
// Project configuration service (singleton for file access)
builder.Services.AddSingleton<IProjectConfigService, RedisProjectConfigService>();
builder.Services.AddSingleton<INuGetPackageLoader, NuGetPackageLoader>();
builder.Services.AddSingleton<INuGetPackageExplorer>(sp =>
{
    var loader = sp.GetRequiredService<INuGetPackageLoader>();
    var logger = sp.GetRequiredService<ILogger<NuGetPackageExplorer>>();
    return new NuGetPackageExplorer(loader, logger);
});


builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
{
    var config = builder.Configuration.GetSection("Redis");
    var connectionString = config.GetValue<string>("ConnectionString") ?? "localhost:6379";

    var configuration = ConfigurationOptions.Parse(connectionString);

    // Apply settings from config with fallback defaults
    configuration.AbortOnConnectFail = config.GetValue("AbortOnConnectFail", true);
    configuration.ConnectTimeout = config.GetValue("ConnectTimeout", 3000);
    configuration.SyncTimeout = config.GetValue("SyncTimeout", 3000);
    configuration.ConnectRetry = config.GetValue("ConnectRetry", 2);
    configuration.KeepAlive = config.GetValue("KeepAlive", 60);
    configuration.AsyncTimeout = config.GetValue("AsyncTimeout", 5000);
    configuration.DefaultDatabase = config.GetValue("DefaultDatabase", 0);
    configuration.ReconnectRetryPolicy = new ExponentialRetry(
        config.GetValue("ReconnectRetryBaseDelay", 5000)
    );

    var logger = sp.GetRequiredService<ILogger<Program>>();

    try
    {
        var multiplexer = ConnectionMultiplexer.Connect(configuration);

        multiplexer.ConnectionFailed += (sender, args) =>
        {
            logger.LogError("Redis connection failed: {EndPoint} - {FailureType}",
                args.EndPoint, args.FailureType);
        };

        multiplexer.ConnectionRestored += (sender, args) =>
        {
            logger.LogInformation("Redis connection restored: {EndPoint}", args.EndPoint);
        };

        multiplexer.ErrorMessage += (sender, args) =>
        {
            logger.LogWarning("Redis error: {Message}", args.Message);
        };

        logger.LogInformation("Redis connected successfully to {ConnectionString}",
            connectionString.Split(',')[0]); // Log endpoint without password

        return multiplexer;
    }
    catch (Exception ex)
    {
        logger.LogCritical(ex, "Failed to connect to Redis at {ConnectionString}. Application cannot start without cache.",
            connectionString.Split(',')[0]);
        throw;
    }
});



// Replace MemoryPackageMetadataCache with Redis
builder.Services.AddSingleton<IPackageMetadataCache, RedisPackageMetadataCache>();


// MCP Server
builder.Services.AddMcpServer()
    .WithHttpTransport()
      .WithToolsFromAssembly();

// Logging
builder.Logging.AddConsole();
builder.Logging.AddDebug();

var app = builder.Build();



// Security headers
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["X-XSS-Protection"] = "1; mode=block";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        await next();
});


app.UseHttpsRedirection();

app.UseAuthorization();

// ✨✨✨ CRITICAL: ADD THESE TWO LINES ✨✨✨
app.UseStaticFiles();
app.UseDefaultFiles();
// ✨✨✨ END CRITICAL SECTION ✨✨✨

app.MapControllers();
app.MapHealthChecks("/health");
app.MapMcp();
app.Run();