using Microsoft.Build.Locator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Roslyn.Mcp.Services;

namespace Roslyn.Mcp;

internal static class Program
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
        roslyn_analyze_dependencies reports symbol counts, the compiled project graph and namespace usage; for NuGet packages use dotnet_package List.
        """;

    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("--version", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine(ServerVersion.Value);
            return 0;
        }

        // MSBuild must be registered before any Roslyn MSBuild workspace type is loaded.
        try
        {
            if (!MSBuildLocator.IsRegistered)
            {
                MSBuildLocator.RegisterDefaults();
            }
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Failed to register MSBuild: {ex.Message}");
            return 1;
        }

        // Stdio is the only transport. --stdio is accepted and ignored so existing client
        // configurations (which pass it, as dotnet-mcp does) keep working.
        var hostArgs = args.Where(arg => !arg.Equals("--stdio", StringComparison.OrdinalIgnoreCase)).ToArray();
        var builder = Host.CreateApplicationBuilder(hostArgs);

        // stdout carries the protocol, so every log level goes to stderr.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Services.AddSingleton<CodeAnalysisService>();
        builder.Services.AddSingleton<SymbolSearchService>();
        builder.Services.AddSingleton<SecurityValidator>();

        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "roslyn",
                    Title = "Roslyn C# Code Analysis",
                    Version = ServerVersion.Value,
                };
                options.ServerInstructions = ServerInstructions;
            })
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        await builder.Build().RunAsync();
        return 0;
    }
}
