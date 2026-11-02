using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Microsoft.Build.Locator;
using Roslyn.Mcp.Services;

namespace Roslyn.Mcp
{
    class Program
    {
        // Sent to clients on initialize. Explains how this server divides work with the .NET MCP
        // server (https://github.com/jongalloway/dotnet-mcp), which is commonly installed alongside it.
        private const string ServerInstructions = """
            Roslyn MCP server: read-only, semantic analysis of C# code using the Roslyn compiler. All tools are prefixed roslyn_.

            It is designed to be used together with the .NET MCP server (dotnet-mcp, tools prefixed dotnet_):
            - Use dotnet_* tools to change or run things: create projects and solutions, add packages and references, restore, build, test, run, publish, format, EF Core, SDK and template information.
            - Use roslyn_* tools to understand code: search symbols, find references, implementations and callers, outline a file, and get compiler diagnostics without building.

            Suggested workflow:
            1. roslyn_search_symbols / roslyn_get_document_outline to locate code, then read only the relevant line ranges.
            2. roslyn_find_references / roslyn_find_callers / roslyn_find_implementations before changing a signature, to see everything that is affected.
            3. After editing a file, roslyn_get_diagnostics with filePath for fast compile feedback; then dotnet_project Build or Test for the authoritative result.

            solutionPath accepts a .sln, .slnx or .csproj file, or a directory containing exactly one of them (the same path given to dotnet_* tools).
            The workspace is cached and refreshes automatically when source, project or restore files change (for example after dotnet_package Add followed by restore). Call roslyn_reload_workspace if results look stale. Many CS0246 "type or namespace not found" diagnostics usually mean packages have not been restored: run dotnet_project Restore.
            """;

        static async Task Main(string[] args)
        {
            // Create a temporary logger for early initialization
            using var loggerFactory = LoggerFactory.Create(builder =>
                builder.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace));
            var tempLogger = loggerFactory.CreateLogger<Program>();

            // Register MSBuild before any workspace operations
            // This is required for Roslyn to find MSBuild
            if (!MSBuildLocator.IsRegistered)
            {
                try
                {
                    MSBuildLocator.RegisterDefaults();
                    tempLogger.LogInformation("MSBuild registered successfully");
                }
                catch (Exception ex)
                {
                    tempLogger.LogError(ex, "Failed to register MSBuild: {Message}", ex.Message);
                    Environment.Exit(1);
                }
            }

            // --stdio runs over standard input/output (the transport dotnet-mcp uses), so both servers can be
            // configured the same way in clients like Claude Desktop, VS Code or Visual Studio.
            // Without it the server listens over HTTP (see Properties/launchSettings.json for the URL).
            var useStdio = args.Contains("--stdio", StringComparer.OrdinalIgnoreCase);
            var hostArgs = args.Where(arg => !arg.Equals("--stdio", StringComparison.OrdinalIgnoreCase)).ToArray();

            try
            {
                if (useStdio)
                {
                    var builder = Host.CreateApplicationBuilder(hostArgs);
                    ConfigureLogging(builder.Logging);
                    ConfigureServices(builder.Services);
                    builder.Services
                        .AddMcpServer(ConfigureMcpServer)
                        .WithStdioServerTransport()
                        .WithToolsFromAssembly();

                    var host = builder.Build();
                    host.Services.GetRequiredService<ILogger<Program>>()
                        .LogInformation("Starting Roslyn MCP Server (stdio)...");
                    await host.RunAsync();
                }
                else
                {
                    var builder = WebApplication.CreateBuilder(hostArgs);
                    ConfigureLogging(builder.Logging);
                    ConfigureServices(builder.Services);
                    builder.Services
                        .AddMcpServer(ConfigureMcpServer)
                        .WithHttpTransport(options =>
                        {
                            options.Stateless = true;
                        })
                        .WithToolsFromAssembly();

                    var app = builder.Build();

                    var logger = app.Services.GetRequiredService<ILogger<Program>>();
                    logger.LogInformation("Starting Roslyn MCP Server (HTTP)...");

                    app.MapMcp();
                    app.UseHttpsRedirection();

                    await app.RunAsync();
                }
            }
            catch (Exception ex)
            {
                tempLogger.LogError(ex, "Failed to start MCP server: {Message}", ex.Message);
                Environment.Exit(1);
            }
        }

        private static void ConfigureLogging(ILoggingBuilder logging)
        {
            // Configure logging for MCP integration - ensure all logs go to stderr
            // (stdout carries the protocol in stdio mode).
            logging.ClearProviders();
            logging.AddConsole(options =>
            {
                options.LogToStandardErrorThreshold = LogLevel.Trace; // All logs to stderr
            });
        }

        private static void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton<CodeAnalysisService>();
            services.AddSingleton<SymbolSearchService>();
            services.AddSingleton<SecurityValidator>();
            services.AddSingleton<DiagnosticLogger>();
            services.AddSingleton<IncrementalAnalyzer>();
            services.AddSingleton<IPersistentCache, FilePersistentCache>();
            services.AddSingleton<MultiLevelCacheManager>();
            services.AddMemoryCache();
        }

        private static void ConfigureMcpServer(McpServerOptions options)
        {
            options.ServerInfo = new Implementation
            {
                Name = "roslyn",
                Title = "Roslyn C# Code Analysis",
                Version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0.0",
            };
            options.ServerInstructions = ServerInstructions;
        }
    }
}
