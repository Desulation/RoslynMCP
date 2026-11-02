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

    [TestMethod]
    public async Task FindReferencesAsync_IncludeDefinition_HasDefinitionEntry()
    {
        var results = await FindReferencesAsync("OrderRepository", includeDefinition: true);

        var definition = Assert.ContainsSingle(results.Where(r => r.IsDefinition && r.ReferenceKind == "Definition"));
        Assert.EndsWith("OrderRepository.cs", definition.DocumentPath);
        Assert.AreEqual(5, definition.LineNumber);
    }

    [TestMethod]
    public async Task FindReferencesAsync_ExcludeDefinition_NoDefinitionEntry()
    {
        var results = await FindReferencesAsync("OrderRepository", includeDefinition: false);

        Assert.IsNotEmpty(results);
        Assert.IsFalse(results.Any(r => r.IsDefinition), "No result should be a definition.");
        Assert.IsFalse(results.Any(r => r.DocumentPath.EndsWith("OrderRepository.cs", StringComparison.Ordinal)),
            "The declaration line must not be reported when includeDefinition is false.");
    }

    [TestMethod]
    public async Task FindReferencesAsync_CrossProject_FindsAppUsage()
    {
        var results = await FindReferencesAsync("OrderService", includeDefinition: false);

        var appUsage = Assert.ContainsSingle(results.Where(r => r.DocumentPath.EndsWith("Program.cs", StringComparison.Ordinal)));
        Assert.AreEqual("Sample.App", appUsage.ProjectName);
        Assert.AreEqual(10, appUsage.LineNumber);
    }

    [TestMethod]
    public async Task FindReferencesAsync_SameLineDefinitionAndReference_KeepsDefinition()
    {
        // OrderService.cs line 16 declares the cascaded override and calls base.Validate on the same line.
        static bool IsOverrideLine(ReferenceResult r) =>
            r.DocumentPath.EndsWith("OrderService.cs", StringComparison.Ordinal) && r.LineNumber == 16;

        var referencesOnly = await FindReferencesAsync("OrderServiceBase.Validate", includeDefinition: false);
        var withDefinitions = await FindReferencesAsync("OrderServiceBase.Validate", includeDefinition: true);

        Assert.IsFalse(Assert.ContainsSingle(referencesOnly.Where(IsOverrideLine)).IsDefinition);
        var overrideLine = Assert.ContainsSingle(withDefinitions.Where(IsOverrideLine));
        Assert.IsTrue(overrideLine.IsDefinition);
        Assert.AreEqual("Definition", overrideLine.ReferenceKind);
    }

    [TestMethod]
    public async Task FindReferencesAsync_MultiTargeted_NoDuplicates()
    {
        var results = await FindReferencesAsync("Order", includeDefinition: true);

        Assert.HasCount(results.Count, results.Select(r => (r.DocumentPath, r.LineNumber)).Distinct().ToList());
    }

    [TestMethod]
    public async Task GetSymbolInfoAsync_Method_HasParametersAndReturnType()
    {
        var info = await s_service.GetSymbolInfoAsync("Classify", TestPaths.SampleSolutionPath, CancellationToken.None);

        Assert.IsNotNull(info);
        CollectionAssert.AreEqual(new[] { "Sample.Core.Order order" }, info.Parameters);
        Assert.AreEqual("string", info.ReturnType);
    }

    [TestMethod]
    public async Task GetSymbolInfoAsync_Ambiguous_ListsOtherMatches()
    {
        var info = await s_service.GetSymbolInfoAsync("Validate", TestPaths.SampleSolutionPath, CancellationToken.None);

        Assert.IsNotNull(info);
        Assert.HasCount(1, info.OtherMatches);
    }

    [TestMethod]
    public async Task GetSymbolInfoAsync_Unknown_Null()
    {
        var info = await s_service.GetSymbolInfoAsync("DoesNotExist", TestPaths.SampleSolutionPath, CancellationToken.None);

        Assert.IsNull(info);
    }

    [TestMethod]
    public async Task FindImplementationsAsync_Interface_FindsClass()
    {
        var results = await s_service.FindImplementationsAsync("IOrderRepository", TestPaths.SampleSolutionPath, CancellationToken.None);

        var result = Assert.ContainsSingle(results);
        Assert.AreEqual("Sample.Core.OrderRepository", result.DisplayName);
        Assert.AreEqual("implements IOrderRepository", result.Relation);
    }

    [TestMethod]
    public async Task FindImplementationsAsync_InterfaceMember_FindsImplementation()
    {
        var results = await s_service.FindImplementationsAsync("IOrderRepository.Store", TestPaths.SampleSolutionPath, CancellationToken.None);

        var result = Assert.ContainsSingle(results);
        Assert.AreEqual("Sample.Core.OrderRepository.Store(Sample.Core.Order)", result.DisplayName);
    }

    [TestMethod]
    public async Task FindImplementationsAsync_BaseClass_FindsDerived()
    {
        var results = await s_service.FindImplementationsAsync("OrderServiceBase", TestPaths.SampleSolutionPath, CancellationToken.None);

        var result = Assert.ContainsSingle(results);
        Assert.AreEqual("Sample.Core.OrderService<T>", result.DisplayName);
    }

    [TestMethod]
    public async Task FindImplementationsAsync_VirtualMember_FindsOverride()
    {
        var results = await s_service.FindImplementationsAsync("OrderServiceBase.Validate", TestPaths.SampleSolutionPath, CancellationToken.None);

        var result = Assert.ContainsSingle(results);
        Assert.AreEqual("Sample.Core.OrderService<T>.Validate(Sample.Core.Order)", result.DisplayName);
        Assert.AreEqual("overrides OrderServiceBase.Validate", result.Relation);
    }

    [TestMethod]
    public async Task FindCallersAsync_Method_FindsCallerInOtherProject()
    {
        var results = await s_service.FindCallersAsync("OrderService.Save", TestPaths.SampleSolutionPath, CancellationToken.None);

        var result = Assert.ContainsSingle(results);
        Assert.AreEqual("Sample.App.Program.Main()", result.DisplayName);
        Assert.EndsWith("Program.cs", result.FilePath);
    }

    [TestMethod]
    public async Task FindCallersAsync_InterfaceMember_FindsCallThroughInterface()
    {
        var results = await s_service.FindCallersAsync("IOrderRepository.Store", TestPaths.SampleSolutionPath, CancellationToken.None);

        var result = Assert.ContainsSingle(results);
        Assert.AreEqual("Sample.Core.OrderService<T>.Save(T)", result.DisplayName);
    }

    [TestMethod]
    public async Task FindCallersAsync_Type_Empty()
    {
        var results = await s_service.FindCallersAsync("Order", TestPaths.SampleSolutionPath, CancellationToken.None);

        Assert.IsEmpty(results);
    }

    private static Task<IReadOnlyList<ReferenceResult>> FindReferencesAsync(string symbolName, bool includeDefinition) =>
        s_service.FindReferencesAsync(symbolName, TestPaths.SampleSolutionPath, includeDefinition, CancellationToken.None);

    private static Task<IReadOnlyList<SymbolSearchResult>> SearchAsync(
        string pattern, string symbolTypes, bool ignoreCase = true) =>
        s_service.SearchSymbolsAsync(
            pattern, TestPaths.SampleSolutionPath, SymbolTypeFilter.Parse(symbolTypes), ignoreCase, CancellationToken.None);
}
