using Microsoft.Extensions.Logging.Abstractions;
using Roslyn.Mcp.Services;

namespace Roslyn.Mcp.Tests.Services;

[TestClass]
public sealed class CodeAnalysisServiceTests
{
    private static WorkspaceManager s_manager = null!;
    private static CodeAnalysisService s_service = null!;

    [ClassInitialize]
    public static void ClassInitialize(TestContext _)
    {
        s_manager = new WorkspaceManager(NullLogger<WorkspaceManager>.Instance, new WorkspaceOptions());
        s_service = new CodeAnalysisService(NullLogger<CodeAnalysisService>.Instance, s_manager);
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        s_manager.Dispose();
    }

    [TestMethod]
    public async Task AnalyzeComplexityAsync_Threshold9_ReturnsClassifyOnly()
    {
        var results = await s_service.AnalyzeComplexityAsync(TestPaths.SampleSolutionPath, 9, CancellationToken.None);

        var result = Assert.ContainsSingle(results);
        Assert.AreEqual("Classify(Order)", result.MemberName);
        Assert.AreEqual("PricingRules", result.ContainingType);
        Assert.AreEqual("Sample.Core", result.Namespace);
        Assert.AreEqual(9, result.Complexity);
        Assert.EndsWith("PricingRules.cs", result.FilePath);
    }

    [TestMethod]
    public async Task AnalyzeComplexityAsync_Threshold10_Empty()
    {
        var results = await s_service.AnalyzeComplexityAsync(TestPaths.SampleSolutionPath, 10, CancellationToken.None);

        Assert.IsEmpty(results);
    }

    [TestMethod]
    public async Task AnalyzeComplexityAsync_MultiTargeted_NoDuplicates()
    {
        var results = await s_service.AnalyzeComplexityAsync(TestPaths.SampleSolutionPath, 1, CancellationToken.None);

        Assert.IsNotEmpty(results);
        var duplicates = results
            .GroupBy(r => (r.FilePath, r.LineNumber))
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key.FilePath}:{g.Key.LineNumber}")
            .ToList();
        Assert.IsEmpty(duplicates, string.Join(", ", duplicates));
        Assert.AreEqual(1, results.Count(r => r.MemberName == "Classify(Order)"));
    }
}
