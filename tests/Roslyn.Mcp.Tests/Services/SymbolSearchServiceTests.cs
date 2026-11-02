using Microsoft.Extensions.Logging.Abstractions;
using Roslyn.Mcp.Models;
using Roslyn.Mcp.Services;

namespace Roslyn.Mcp.Tests.Services;

[TestClass]
public sealed class SymbolSearchServiceTests
{
    private static WorkspaceManager s_manager = null!;
    private static SymbolSearchService s_service = null!;

    [ClassInitialize]
    public static void ClassInitialize(TestContext _)
    {
        s_manager = new WorkspaceManager(NullLogger<WorkspaceManager>.Instance, new WorkspaceOptions());
        s_service = new SymbolSearchService(
            NullLogger<SymbolSearchService>.Instance, s_manager, new SymbolResolver(s_manager));
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        s_manager.Dispose();
    }

    [TestMethod]
    public async Task SearchSymbolsAsync_SuffixWildcard_FindsServiceTypes()
    {
        var results = await SearchAsync("*Service", "class");

        var result = Assert.ContainsSingle(results);
        Assert.AreEqual("OrderService", result.Name);
    }

    [TestMethod]
    public async Task SearchSymbolsAsync_PrefixWildcard_FindsMethods()
    {
        var results = await SearchAsync("Val*", "method");

        Assert.HasCount(2, results);
        Assert.IsTrue(results.All(r => r.Name == "Validate"));
        CollectionAssert.AreEquivalent(
            new[] { "Sample.Core.OrderService<T>.Validate(Sample.Core.Order)", "Sample.Core.OrderServiceBase.Validate(Sample.Core.Order)" },
            results.Select(r => r.FullName).ToArray());
    }

    [TestMethod]
    public async Task SearchSymbolsAsync_PrefixWildcard_DoesNotMatchMembersByOwnerName()
    {
        var results = await SearchAsync("Order*", "method");

        var names = results.Select(r => r.Name).ToList();
        Assert.DoesNotContain("Save", names);
        Assert.DoesNotContain("Store", names);
        Assert.DoesNotContain("Validate", names);
    }

    [TestMethod]
    public async Task SearchSymbolsAsync_QuestionMark_MatchesOneChar()
    {
        var results = await SearchAsync("?rder", "class,record");

        var result = Assert.ContainsSingle(results);
        Assert.AreEqual("Sample.Core.Order", result.FullName);
    }

    [TestMethod]
    public async Task SearchSymbolsAsync_InterfaceOnly_ReturnsIOrderRepository()
    {
        var results = await SearchAsync("*", "interface");

        var result = Assert.ContainsSingle(results);
        Assert.AreEqual("IOrderRepository", result.Name);
    }

    [TestMethod]
    public async Task SearchSymbolsAsync_EnumOnly_ReturnsOrderStatus()
    {
        var results = await SearchAsync("*", "enum");

        var result = Assert.ContainsSingle(results);
        Assert.AreEqual("OrderStatus", result.Name);
    }

    [TestMethod]
    public async Task SearchSymbolsAsync_PropertyOnly_ReturnsSavedCount()
    {
        var results = await SearchAsync("Saved*", "property");

        var result = Assert.ContainsSingle(results);
        Assert.AreEqual("SavedCount", result.Name);
        Assert.AreEqual(Microsoft.CodeAnalysis.SymbolKind.Property, result.SymbolKind);
    }

    [TestMethod]
    public async Task SearchSymbolsAsync_GenericPattern_Matches()
    {
        var results = await SearchAsync("OrderService<*>", "class");

        var result = Assert.ContainsSingle(results);
        Assert.AreEqual("Sample.Core.OrderService<T>", result.FullName);
    }

    [TestMethod]
    public async Task SearchSymbolsAsync_CaseSensitive_NoMatch()
    {
        var results = await SearchAsync("orderservice", "class", ignoreCase: false);

        Assert.IsEmpty(results);
    }

    [TestMethod]
    public async Task SearchSymbolsAsync_ExactName_RankedFirst()
    {
        var results = await SearchAsync("Order*", "class,record");

        Assert.IsGreaterThan(1, results.Count);
        Assert.AreEqual("Order", results[0].Name);
    }

    [TestMethod]
    public async Task SearchSymbolsAsync_StarOnly_ReturnsAllDeclaredSymbols()
    {
        var results = await SearchAsync("*", string.Join(",", SymbolTypeFilter.AcceptedWords));

        Assert.IsGreaterThanOrEqualTo(20, results.Count);
        var fixtureDir = Path.GetFullPath(TestPaths.SampleSolutionDir) + Path.DirectorySeparatorChar;
        foreach (var result in results)
        {
            Assert.IsTrue(
                Path.GetFullPath(result.FilePath).StartsWith(fixtureDir, StringComparison.OrdinalIgnoreCase),
                $"{result.FullName} is declared outside the fixture: '{result.FilePath}'");
        }
    }

    [TestMethod]
    public async Task SearchSymbolsAsync_MultiTargeted_NoDuplicates()
    {
        var results = await SearchAsync("Order", "record");

        var result = Assert.ContainsSingle(results);
        Assert.AreEqual("Sample.Core.Order", result.FullName);
    }

    [TestMethod]
    public async Task SearchSymbolsAsync_Cancelled_Throws()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => s_service.SearchSymbolsAsync(
            "*", TestPaths.SampleSolutionPath, SymbolTypeFilter.Parse("class"), ignoreCase: true, cts.Token));
    }

    private static Task<IReadOnlyList<SymbolSearchResult>> SearchAsync(
        string pattern, string symbolTypes, bool ignoreCase = true) =>
        s_service.SearchSymbolsAsync(
            pattern, TestPaths.SampleSolutionPath, SymbolTypeFilter.Parse(symbolTypes), ignoreCase, CancellationToken.None);
}
