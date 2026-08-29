using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;
using Odyssey.Agent;
using Odyssey.Core;
using Odyssey.Infrastructure;
using Odyssey.Mcp;
using Odyssey.Search;

if (args.Contains("--http", StringComparer.OrdinalIgnoreCase))
    await RunHttpAsync(args);
else
    await RunStdioAsync(args);

static async Task RunStdioAsync(string[] args)
{
    var builder = Host.CreateApplicationBuilder(args);
    ConfigureLogging(builder.Logging);
    AddOdysseyServices(builder.Services);
    builder.Services.AddMcpServer()
        .WithStdioServerTransport()
        .WithTools<OdysseyMcpTools>();
    var host = builder.Build();
    await host.Services.GetRequiredService<OdysseyMcpFacade>().InitializeAsync();
    await host.RunAsync();
}

static async Task RunHttpAsync(string[] args)
{
    var token = Environment.GetEnvironmentVariable("ODYSSEY_MCP_TOKEN");
    if (string.IsNullOrWhiteSpace(token) || Encoding.UTF8.GetByteCount(token) < 32)
        throw new InvalidOperationException("ODYSSEY_MCP_TOKEN with at least 32 UTF-8 bytes is required for HTTP mode.");

    var urls = Environment.GetEnvironmentVariable("ODYSSEY_MCP_URLS") ?? "http://127.0.0.1:47831";
    if (!IsLoopbackOnly(urls) && Environment.GetEnvironmentVariable("ODYSSEY_MCP_ALLOW_REMOTE") != "1")
        throw new InvalidOperationException("Non-loopback MCP binding requires ODYSSEY_MCP_ALLOW_REMOTE=1 and a TLS/OAuth reverse proxy.");
    var allowedHosts = (Environment.GetEnvironmentVariable("ODYSSEY_MCP_ALLOWED_HOSTS") ?? "localhost;127.0.0.1;::1")
        .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var allowedOrigins = (Environment.GetEnvironmentVariable("ODYSSEY_MCP_ALLOWED_ORIGINS") ?? string.Empty)
        .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    var webArgs = args.Where(arg => !string.Equals(arg, "--http", StringComparison.OrdinalIgnoreCase)).ToArray();
    var builder = WebApplication.CreateBuilder(webArgs);
    builder.WebHost.UseUrls(urls.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
    ConfigureLogging(builder.Logging);
    AddOdysseyServices(builder.Services);
    builder.Services.AddMcpServer()
        .WithHttpTransport(options => options.Stateless = true)
        .WithTools<OdysseyMcpTools>();
    var app = builder.Build();
    await app.Services.GetRequiredService<OdysseyMcpFacade>().InitializeAsync();

    app.Use(async (context, next) =>
    {
        if (!allowedHosts.Contains(context.Request.Host.Host))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        var origin = context.Request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin) && !allowedOrigins.Contains(origin))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        var authorization = context.Request.Headers.Authorization.ToString();
        if (!FixedTimeBearerEquals(authorization, token))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return;
        }
        await next(context);
    });
    app.MapMcp("/mcp");
    await app.RunAsync();
}

static void AddOdysseyServices(IServiceCollection services)
{
    services.AddSingleton(SystemPerformanceProfile.Current);
    services.AddSingleton<ApplicationStorage>();
    services.AddSingleton<SqliteConnectionFactory>();
    services.AddSingleton<IOdysseyStore, SqliteOdysseyStore>();
    services.AddSingleton<ISearchService, SqliteSearchService>();
    services.AddSingleton<AgentAccessPolicyService>();
    services.AddSingleton<AgentOperationStore>();
    services.AddSingleton<AgentOperationService>();
    services.AddSingleton<OdysseyMcpFacade>();
}

static void ConfigureLogging(ILoggingBuilder logging)
{
    logging.ClearProviders();
    logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
}

static bool FixedTimeBearerEquals(string actual, string token)
{
    var expectedBytes = Encoding.UTF8.GetBytes("Bearer " + token);
    var actualBytes = Encoding.UTF8.GetBytes(actual);
    return expectedBytes.Length == actualBytes.Length && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
}

static bool IsLoopbackOnly(string urls)
{
    foreach (var value in urls.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        if (!string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) && uri.Host is not "127.0.0.1" and not "::1" and not "[::1]")
            return false;
    }
    return true;
}
