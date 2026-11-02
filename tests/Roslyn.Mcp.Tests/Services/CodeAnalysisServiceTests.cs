using Microsoft.Extensions.Logging.Abstractions;
using Roslyn.Mcp.Models;
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

    [TestMethod]
    public async Task AnalyzeDependenciesAsync_ProjectGraph_AppReferencesCore()
    {
        var analysis = await s_service.AnalyzeDependenciesAsync(TestPaths.SampleSolutionPath, 20, CancellationToken.None);

        Assert.AreEqual("Sample", analysis.SolutionName);
        var app = analysis.Projects.Single(p => p.Name == "Sample.App");
        CollectionAssert.AreEqual(new[] { "Sample.Core(net10.0)" }, app.ProjectReferences);
    }

    [TestMethod]
    public async Task AnalyzeDependenciesAsync_SymbolCounts_Positive()
    {
        var analysis = await s_service.AnalyzeDependenciesAsync(TestPaths.SampleSolutionPath, 20, CancellationToken.None);

        Assert.IsNotEmpty(analysis.Projects);
        foreach (var project in analysis.Projects)
        {
            Assert.IsGreaterThan(0, project.TotalSymbols, project.Name);
            Assert.IsLessThanOrEqualTo(project.TotalSymbols, project.PublicSymbols, project.Name);
            Assert.IsLessThanOrEqualTo(project.TotalSymbols, project.PublicSymbols + project.InternalSymbols, project.Name);
        }
    }

    [TestMethod]
    public async Task AnalyzeDependenciesAsync_NamespaceUsage_CountsUsings()
    {
        var analysis = await s_service.AnalyzeDependenciesAsync(TestPaths.SampleSolutionPath, 20, CancellationToken.None);

        var system = analysis.NamespaceUsages.Single(n => n.Namespace == "System");
        Assert.IsGreaterThanOrEqualTo(3, system.UsageCount);
        Assert.IsTrue(analysis.NamespaceUsages.Any(n => n.Namespace == "Sample.Core"));
    }

    [TestMethod]
    public async Task AnalyzeDependenciesAsync_MultiTargeted_CountsEachFileOnce()
    {
        var analysis = await s_service.AnalyzeDependenciesAsync(TestPaths.SampleSolutionPath, 20, CancellationToken.None);

        // Program.cs, OrderService.cs and PricingRules.cs; Sample.Core's files are compiled twice
        // and MSBuild-generated AssemblyInfo files add 'using System;', neither of which counts.
        Assert.AreEqual(3, analysis.NamespaceUsages.Single(n => n.Namespace == "System").UsageCount);
        Assert.AreEqual(1, analysis.NamespaceUsages.Single(n => n.Namespace == "System.Collections.Generic").UsageCount);
        Assert.IsFalse(analysis.NamespaceUsages.Any(n => n.Namespace == "System.Reflection"));
    }

    [TestMethod]
    public async Task AnalyzeDependenciesAsync_MaxNamespaces_Caps()
    {
        var analysis = await s_service.AnalyzeDependenciesAsync(TestPaths.SampleSolutionPath, 1, CancellationToken.None);

        var usage = Assert.ContainsSingle(analysis.NamespaceUsages);
        Assert.AreEqual("System", usage.Namespace);
    }

    [TestMethod]
    public async Task AnalyzeDependenciesAsync_NoAssemblyNames()
    {
        var analysis = await s_service.AnalyzeDependenciesAsync(TestPaths.SampleSolutionPath, 20, CancellationToken.None);

        var references = analysis.Projects.SelectMany(p => p.ProjectReferences).ToList();
        Assert.DoesNotContain("System.Runtime", references);
        Assert.DoesNotContain("netstandard", references);
        var listProperties = typeof(DependencyAnalysis).GetProperties()
            .Where(p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(List<>))
            .Select(p => p.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        CollectionAssert.AreEqual(new[] { "NamespaceUsages", "Projects" }, listProperties);
    }
}
