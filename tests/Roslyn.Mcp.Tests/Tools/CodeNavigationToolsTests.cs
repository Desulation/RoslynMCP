using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;
using Roslyn.Mcp.Services;
using Roslyn.Mcp.Tools;

namespace Roslyn.Mcp.Tests.Tools;

[TestClass]
public sealed class CodeNavigationToolsTests
{
    private const string HintPrefix = "\n(Workspace loaded with ";

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

    private static readonly SecurityValidator s_validator = new();
    private static readonly NullLogger<CodeNavigationTools> s_logger = NullLogger<CodeNavigationTools>.Instance;

    // Shared by tests on the clean checked-in fixture, which never carries a load hint.
    private static Services s_services = null!;

    [ClassInitialize]
    public static void ClassInitialize(TestContext _)
    {
        s_services = new Services();
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        s_services.Dispose();
    }

    [TestMethod]
    public async Task SearchSymbols_RelativeSolutionPath_ReturnsInvalidPathError()
    {
        var result = await CodeNavigationTools.SearchSymbols(
            s_validator, s_services.Search, s_services.Workspaces, s_logger, "Order*", "Sample.slnx");

        Assert.StartsWith("Error: Invalid solution path", result);
    }

    [TestMethod]
    public async Task SearchSymbols_UnknownSymbolType_ReturnsAcceptedList()
    {
        var result = await CodeNavigationTools.SearchSymbols(
            s_validator, s_services.Search, s_services.Workspaces, s_logger, "Order*", TestPaths.SampleSolutionPath,
            symbolTypes: "class,widget");

        Assert.StartsWith("Error: ", result);
        Assert.Contains("unknown symbol type 'widget'", result);
        Assert.Contains("Accepted:", result);
    }

    [TestMethod]
    public async Task SearchSymbols_EmptyPattern_ReturnsPatternError()
    {
        var result = await CodeNavigationTools.SearchSymbols(
            s_validator, s_services.Search, s_services.Workspaces, s_logger, " ", TestPaths.SampleSolutionPath);

        Assert.AreEqual("Error: Search pattern must not be empty.", result);
    }

    [TestMethod]
    public async Task GetDiagnostics_BadSeverity_ReturnsError()
    {
        var result = await CodeNavigationTools.GetDiagnostics(
            s_validator, s_services.Analysis, s_services.Workspaces, s_logger, TestPaths.SampleSolutionPath,
            minimumSeverity: "Fatal");

        Assert.AreEqual("Error: minimumSeverity must be Error, Warning or Info.", result);
    }

    [TestMethod]
    public async Task GetDocumentOutline_FileNotInSolution_PassesMessageThrough()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"roslyn-mcp-tests-{Guid.NewGuid():N}.cs");
        File.WriteAllText(filePath, "public static class Stray { }\n");
        try
        {
            var result = await CodeNavigationTools.GetDocumentOutline(
                s_validator, s_services.Analysis, s_services.Workspaces, s_logger, filePath, TestPaths.SampleSolutionPath);

            Assert.AreEqual($"Error: '{filePath}' is not a document in the solution.", result);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [TestMethod]
    public async Task FindImplementations_CancelledToken_ReturnsCancelledMessage()
    {
        var result = await CodeNavigationTools.FindImplementations(
            s_validator, s_services.Search, s_services.Workspaces, s_logger, "IOrderRepository",
            TestPaths.SampleSolutionPath, new CancellationToken(canceled: true));

        Assert.AreEqual("Error: The find implementations operation was cancelled or timed out.", result);
    }

    [TestMethod]
    public async Task AnyTool_SolutionWithLoadWarnings_AppendsHintOnce()
    {
        using var copy = CreateCopyWithLoadWarnings();
        using var services = new Services();
        await services.Workspaces.GetSolutionAsync(copy.SolutionPath);

        // A failed call neither carries nor consumes the pending hint.
        var failed = await CodeNavigationTools.FindImplementations(
            s_validator, services.Search, services.Workspaces, s_logger, "IOrderRepository",
            copy.SolutionPath, new CancellationToken(canceled: true));
        var first = await GetSymbolInfoAsync(services, copy.SolutionPath);
        var second = await GetSymbolInfoAsync(services, copy.SolutionPath);

        Assert.AreEqual("Error: The find implementations operation was cancelled or timed out.", failed);
        Assert.IsFalse(first.StartsWith("Error:", StringComparison.Ordinal), first);
        var match = Regex.Match(
            first,
            @"\n\(Workspace loaded with (\d+) MSBuild warnings; call roslyn_reload_workspace to see them\.\)\z");
        Assert.IsTrue(match.Success, first);
        Assert.IsGreaterThanOrEqualTo(1, int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.AreEqual(1, Regex.Count(first, Regex.Escape(HintPrefix)), first);
        Assert.IsFalse(second.StartsWith("Error:", StringComparison.Ordinal), second);
        Assert.DoesNotContain(HintPrefix, second);
    }

    [TestMethod]
    public async Task ReloadWorkspace_SolutionWithLoadWarnings_ListsWarningsWithoutHint()
    {
        using var copy = CreateCopyWithLoadWarnings();
        using var services = new Services();

        var reload = await CodeNavigationTools.ReloadWorkspace(
            s_validator, services.Workspaces, s_logger, copy.SolutionPath);
        var next = await GetSymbolInfoAsync(services, copy.SolutionPath);

        Assert.StartsWith("Reloaded ", reload);
        Assert.Contains("MSBuild load warnings:", reload);
        Assert.Contains("Nope", reload);
        Assert.DoesNotContain(HintPrefix, reload);
        Assert.IsFalse(next.StartsWith("Error:", StringComparison.Ordinal), next);
        Assert.DoesNotContain(HintPrefix, next);
    }

    [TestMethod]
    public async Task ExecuteAsync_HintLookupThrows_ReturnsActionResult()
    {
        var directory = Directory.CreateTempSubdirectory("roslyn-mcp-tests-");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "A.csproj"), "<Project />");

            // After the action, the directory holds two projects, so resolving it for the hint throws.
            var result = await CodeNavigationTools.ExecuteAsync(
                s_validator, s_services.Workspaces, s_logger, directory.FullName, "test", () =>
                {
                    File.WriteAllText(Path.Combine(directory.FullName, "B.csproj"), "<Project />");
                    return Task.FromResult("result");
                });

            Assert.AreEqual("result", result);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void AllTools_HaveReadOnlyAnnotations()
    {
        var tools = typeof(CodeNavigationTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(m => (Method: m, Attribute: m.GetCustomAttribute<McpServerToolAttribute>()))
            .Where(t => t.Attribute != null)
            .ToList();

        CollectionAssert.AreEquivalent(ExpectedToolNames, tools.Select(t => t.Attribute!.Name).ToArray());
        foreach (var (method, attribute) in tools)
        {
            Assert.IsTrue(attribute!.ReadOnly, attribute.Name);
            Assert.IsTrue(attribute.Idempotent, attribute.Name);
            Assert.IsFalse(attribute.Destructive, attribute.Name);
            Assert.IsFalse(attribute.OpenWorld, attribute.Name);
            Assert.StartsWith("roslyn_", attribute.Name!);

            var parameters = method.GetParameters();
            var last = parameters[^1];
            Assert.AreEqual(typeof(CancellationToken), last.ParameterType, attribute.Name);
            Assert.AreEqual("cancellationToken", last.Name, attribute.Name);
            Assert.IsTrue(last.HasDefaultValue, attribute.Name);
            Assert.IsTrue(
                parameters.Any(p => p.ParameterType == typeof(WorkspaceManager)),
                $"{attribute.Name} takes no WorkspaceManager");
        }
    }

    private static Task<string> GetSymbolInfoAsync(Services services, string solutionPath) =>
        CodeNavigationTools.GetSymbolInfo(
            s_validator, services.Search, services.Workspaces, s_logger, "OrderStatus", solutionPath);

    // Task 4's recipe: a reference to a project that does not exist makes MSBuild report a load warning.
    private static FixtureCopy CreateCopyWithLoadWarnings()
    {
        var copy = FixtureCopy.Create();
        try
        {
            var appProject = copy.AppFile("Sample.App.csproj");
            const string coreReference = "<ProjectReference Include=\"../Sample.Core/Sample.Core.csproj\" />";
            var projectXml = File.ReadAllText(appProject);
            Assert.Contains(coreReference, projectXml);
            File.WriteAllText(
                appProject,
                projectXml.Replace(
                    coreReference,
                    coreReference + "\n    <ProjectReference Include=\"../Nope/Nope.csproj\" />",
                    StringComparison.Ordinal));
            DotnetCli.Restore(copy.SolutionPath, ignoreFailure: true);
            return copy;
        }
        catch
        {
            copy.Dispose();
            throw;
        }
    }

    // The real service graph as Program.cs registers it, with refresh checks on every call.
    private sealed class Services : IDisposable
    {
        public Services()
        {
            Workspaces = new WorkspaceManager(
                NullLogger<WorkspaceManager>.Instance, new WorkspaceOptions { RefreshInterval = TimeSpan.Zero });
            Search = new SymbolSearchService(
                NullLogger<SymbolSearchService>.Instance, Workspaces, new SymbolResolver(Workspaces));
            Analysis = new CodeAnalysisService(NullLogger<CodeAnalysisService>.Instance, Workspaces);
        }

        public WorkspaceManager Workspaces { get; }

        public SymbolSearchService Search { get; }

        public CodeAnalysisService Analysis { get; }

        public void Dispose() => Workspaces.Dispose();
    }
}
