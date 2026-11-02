using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using Roslyn.Mcp.Services;

namespace Roslyn.Mcp.Tests.Services;

[TestClass]
public sealed class SymbolResolverTests
{
    private static WorkspaceManager s_manager = null!;
    private static SymbolResolver s_resolver = null!;

    [ClassInitialize]
    public static void ClassInitialize(TestContext _)
    {
        s_manager = new WorkspaceManager(NullLogger<WorkspaceManager>.Instance, new WorkspaceOptions());
        s_resolver = new SymbolResolver(s_manager);
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        s_manager.Dispose();
    }

    [TestMethod]
    public async Task FindByNameAsync_SimpleName_FindsType()
    {
        var symbols = await s_resolver.FindByNameAsync(TestPaths.SampleSolutionPath, "OrderService");

        var symbol = Assert.ContainsSingle(symbols);
        Assert.IsInstanceOfType<INamedTypeSymbol>(symbol);
        Assert.AreEqual("OrderService", symbol.Name);
    }

    [TestMethod]
    public async Task FindByNameAsync_TypeDotMember_FindsMember()
    {
        var symbols = await s_resolver.FindByNameAsync(TestPaths.SampleSolutionPath, "OrderRepository.Store");

        var symbol = Assert.ContainsSingle(symbols);
        Assert.IsInstanceOfType<IMethodSymbol>(symbol);
        Assert.AreEqual("OrderRepository", symbol.ContainingType.Name);
    }

    [TestMethod]
    public async Task FindByNameAsync_NamespaceDotType_FindsType()
    {
        var symbols = await s_resolver.FindByNameAsync(TestPaths.SampleSolutionPath, "Sample.Core.Order");

        var symbol = Assert.ContainsSingle(symbols);
        Assert.IsInstanceOfType<INamedTypeSymbol>(symbol);
        Assert.AreEqual("Sample.Core.Order", symbol.ToDisplayString());
    }

    [TestMethod]
    public async Task FindByNameAsync_GenericOwner_FindsMember()
    {
        var symbols = await s_resolver.FindByNameAsync(TestPaths.SampleSolutionPath, "OrderService.Save");

        var symbol = Assert.ContainsSingle(symbols);
        Assert.IsInstanceOfType<IMethodSymbol>(symbol);
        Assert.AreEqual("OrderService", symbol.ContainingType.Name);
    }

    [TestMethod]
    public async Task FindByNameAsync_DifferentCase_Finds()
    {
        var symbols = await s_resolver.FindByNameAsync(TestPaths.SampleSolutionPath, "orderservice");

        var symbol = Assert.ContainsSingle(symbols);
        Assert.AreEqual("OrderService", symbol.Name);
    }

    [TestMethod]
    public async Task FindByNameAsync_Unknown_Empty()
    {
        var symbols = await s_resolver.FindByNameAsync(TestPaths.SampleSolutionPath, "NoSuchSymbol");

        Assert.IsEmpty(symbols);
    }

    [TestMethod]
    public async Task FindByNameAsync_MultiTargetedProject_OneResultPerSymbol()
    {
        var symbols = await s_resolver.FindByNameAsync(TestPaths.SampleSolutionPath, "Order");

        var symbol = Assert.ContainsSingle(symbols);
        Assert.AreEqual("Sample.Core.Order", symbol.ToDisplayString());
    }

    [TestMethod]
    public async Task FindByNameAsync_Validate_FindsBaseAndOverride()
    {
        var symbols = await s_resolver.FindByNameAsync(TestPaths.SampleSolutionPath, "Validate");

        Assert.HasCount(2, symbols);
        CollectionAssert.AreEquivalent(
            new[] { "OrderService", "OrderServiceBase" },
            symbols.Select(s => s.ContainingType.Name).ToArray());
    }

    [TestMethod]
    public async Task MatchesName_TrimsWhitespaceAndParentheses_Matches()
    {
        var save = Assert.ContainsSingle(await s_resolver.FindByNameAsync(TestPaths.SampleSolutionPath, "Save"));

        Assert.IsTrue(SymbolResolver.MatchesName(save, " Save() "));
    }

    [TestMethod]
    public async Task MatchesName_AccessorsExcluded()
    {
        var solution = await s_manager.GetSolutionAsync(TestPaths.SampleSolutionPath);
        var core = solution.Projects.First(p => p.Name.StartsWith("Sample.Core", StringComparison.Ordinal));
        var compilation = await core.GetCompilationAsync();
        Assert.IsNotNull(compilation);

        var names = SymbolResolver.EnumerateDeclaredSymbols(compilation).Select(s => s.Name).ToList();

        Assert.Contains("SavedCount", names);
        Assert.DoesNotContain("get_SavedCount", names);
    }
}
