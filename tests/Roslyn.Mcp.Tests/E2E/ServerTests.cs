using System.Diagnostics;
using System.Xml.Linq;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Roslyn.Mcp.Tests.E2E;

/// <summary>
/// Starts the built roslyn-mcp executable and talks to it over stdio with the MCP client SDK.
/// </summary>
[TestClass]
public sealed class ServerTests
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(60);

    // The 2.2.0 client waits this long for the server to exit on dispose before killing it, even
    // though roslyn-mcp itself exits within milliseconds of stdin EOF. The default (5 s) per client
    // would dominate the run time of this class.
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(1);

    private static readonly string[] ExpectedToolNames =
    {
        "roslyn_search_symbols",
        "roslyn_find_references",
        "roslyn_get_symbol_info",
        "roslyn_find_implementations",
        "roslyn_find_callers",
        "roslyn_get_document_outline",
        "roslyn_get_diagnostics",
        "roslyn_analyze_dependencies",
        "roslyn_analyze_code_complexity",
        "roslyn_reload_workspace",
    };

    private static McpClient? s_client;

    [ClassInitialize]
    public static async Task ClassInitialize(TestContext _)
    {
        if (!File.Exists(TestPaths.ServerExePath))
            Assert.Inconclusive($"Server executable not built: {TestPaths.ServerExePath}");

        // When initialization fails, CreateAsync stops the child process itself before throwing.
        s_client = await StartClientAsync([]);
    }

    [ClassCleanup]
    public static async Task ClassCleanup()
    {
        if (s_client is not null)
            await s_client.DisposeAsync();
    }

    [TestMethod]
    public async Task ToolsList_ExactlyTenRoslynTools_AllReadOnly()
    {
        using var timeout = new CancellationTokenSource(CallTimeout);

        var tools = await Client.ListToolsAsync(cancellationToken: timeout.Token);

        CollectionAssert.AreEquivalent(ExpectedToolNames, tools.Select(t => t.Name).ToArray());
        foreach (var tool in tools)
            Assert.IsTrue(tool.ProtocolTool.Annotations?.ReadOnlyHint, tool.Name);
    }

    [TestMethod]
    public void Initialize_InstructionsMentionDotnetTools()
    {
        Assert.Contains("dotnet_", Client.ServerInstructions ?? string.Empty);
    }

    [TestMethod]
    public async Task CallTool_SearchSymbols_ReturnsOrderService()
    {
        using var timeout = new CancellationTokenSource(CallTimeout);

        var result = await Client.CallToolAsync(
            "roslyn_search_symbols",
            new Dictionary<string, object?>
            {
                ["pattern"] = "OrderService",
                ["solutionPath"] = TestPaths.SampleSolutionPath,
                ["symbolTypes"] = "class",
            },
            cancellationToken: timeout.Token);

        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        Assert.IsFalse(result.IsError ?? false, text);
        Assert.IsFalse(text.StartsWith("Error:", StringComparison.Ordinal), text);
        Assert.Contains("OrderService", text);
    }

    [TestMethod]
    public async Task Version_PrintsDirectoryBuildPropsVersion()
    {
        var propsPath = Path.GetFullPath(Path.Combine(TestPaths.FixturesDir, "..", "..", "Directory.Build.props"));
        var expected = XDocument.Load(propsPath).Descendants("Version").Single().Value.Trim();
        var startInfo = new ProcessStartInfo(TestPaths.ServerExePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--version");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start roslyn-mcp --version.");
        try
        {
            // Read both streams concurrently and bound everything by the timeout, so a server that
            // never exits (or never closes stdout) fails the test instead of hanging it.
            using var timeout = new CancellationTokenSource(CallTimeout);
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                Assert.Fail($"roslyn-mcp --version did not exit within {CallTimeout.TotalSeconds} s.");
            }

            Assert.AreEqual(0, process.ExitCode, await stderr);
            Assert.AreEqual(expected, (await stdout).Trim());
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }

    [TestMethod]
    public async Task StdioFlag_StillAccepted()
    {
        using var timeout = new CancellationTokenSource(CallTimeout);
        await using var client = await StartClientAsync(["--stdio"]);

        var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);

        Assert.HasCount(ExpectedToolNames.Length, tools);
    }

    private static McpClient Client =>
        s_client ?? throw new InvalidOperationException("ClassInitialize did not start the server.");

    private static async Task<McpClient> StartClientAsync(string[] arguments)
    {
        // Inherit the environment minus MSBuildLocator's variables, so the server registers MSBuild itself.
        var environment = DotnetCli.InheritedMSBuildVariables.ToDictionary(name => name, string? (_) => null);
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "roslyn-mcp",
            Command = TestPaths.ServerExePath,
            Arguments = arguments,
            EnvironmentVariables = environment,
            ShutdownTimeout = ShutdownTimeout,
        });

        using var timeout = new CancellationTokenSource(CallTimeout);
        return await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
    }
}
