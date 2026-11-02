using Microsoft.CodeAnalysis;
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

    [TestMethod]
    public async Task GetDiagnosticsAsync_WholeSolution_FindsCS0219AtLine7()
    {
        var results = await s_service.GetDiagnosticsAsync(
            TestPaths.SampleSolutionPath, null, DiagnosticSeverity.Hidden, CancellationToken.None);

        // Sample.Core compiles for two target frameworks; the warning is still reported once.
        var unused = Assert.ContainsSingle(results.Where(d => d.Id == "CS0219"));
        Assert.AreEqual(7, unused.LineNumber);
        Assert.AreEqual(13, unused.ColumnNumber);
        Assert.EndsWith("Scratch.cs", unused.FilePath);
    }

    [TestMethod]
    public async Task GetDiagnosticsAsync_ErrorOnly_HidesWarning()
    {
        var results = await s_service.GetDiagnosticsAsync(
            TestPaths.SampleSolutionPath, null, DiagnosticSeverity.Error, CancellationToken.None);

        Assert.IsFalse(results.Any(d => d.Id == "CS0219"));
        Assert.IsTrue(results.All(d => d.Severity == DiagnosticSeverity.Error));
    }

    [TestMethod]
    public async Task GetDiagnosticsAsync_FilePath_ScopesToFile()
    {
        var results = await s_service.GetDiagnosticsAsync(
            TestPaths.SampleSolutionPath, TestPaths.CoreFile("Order.cs"), DiagnosticSeverity.Hidden, CancellationToken.None);

        Assert.IsFalse(results.Any(d => d.Id == "CS0219"));
        Assert.IsTrue(results.All(d => d.FilePath.EndsWith("Order.cs", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task GetDiagnosticsAsync_FileNotInSolution_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), $"roslyn-mcp-tests-{Guid.NewGuid():N}.cs");
        File.WriteAllText(path, "public static class Outside { }\n");
        try
        {
            var ex = await Assert.ThrowsExactlyAsync<ArgumentException>(() => s_service.GetDiagnosticsAsync(
                TestPaths.SampleSolutionPath, path, DiagnosticSeverity.Hidden, CancellationToken.None));

            StringAssert.Contains(ex.Message, path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task GetDiagnosticsAsync_UnrestoredProject_ReportsRestoreHint()
    {
        using var copy = FixtureCopy.Create();
        var appProject = copy.AppFile("Sample.App.csproj");
        const string coreReference = "<ProjectReference Include=\"../Sample.Core/Sample.Core.csproj\" />";
        var projectXml = File.ReadAllText(appProject);
        Assert.Contains(coreReference, projectXml);
        File.WriteAllText(
            appProject,
            projectXml.Replace(
                coreReference,
                coreReference + "\n    <PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\" />",
                StringComparison.Ordinal));
        // Twelve type-position uses (CS0246 each); the copy is deliberately not restored again.
        var fields = Enumerable.Range(1, 12)
            .Select(i => $"    public static JsonSerializerSettings Settings{i:D2} = null!;");
        File.WriteAllText(
            copy.AppFile("Json.cs"),
            "using Newtonsoft.Json;\n\nnamespace Sample.App;\n\npublic static class Json\n{\n" +
            string.Join("\n", fields) + "\n}\n");
        using var manager = new WorkspaceManager(NullLogger<WorkspaceManager>.Instance, new WorkspaceOptions());
        var service = new CodeAnalysisService(NullLogger<CodeAnalysisService>.Instance, manager);

        var results = await service.GetDiagnosticsAsync(
            copy.SolutionPath, null, DiagnosticSeverity.Hidden, CancellationToken.None);

        Assert.IsGreaterThan(10, results.Count(d => d.Id == "CS0246"));
        var hint = results[0];
        Assert.AreEqual("RMCP001", hint.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, hint.Severity);
        Assert.AreEqual(
            "Many 'type or namespace not found' errors: packages are probably not restored. Run dotnet_project Restore.",
            hint.Message);
        Assert.AreEqual("Sample.App", hint.ProjectName);
        Assert.AreEqual(string.Empty, hint.FilePath);
        Assert.AreEqual(1, results.Count(d => d.Id == "RMCP001"));
    }

    [TestMethod]
    public async Task GetDocumentOutlineAsync_OrderService_KindsAndDepths()
    {
        var path = TestPaths.CoreFile("OrderService.cs");

        var outline = await s_service.GetDocumentOutlineAsync(TestPaths.SampleSolutionPath, path, CancellationToken.None);

        Assert.HasCount(7, outline);
        AssertEntry(outline[0], 0, "Namespace", "Sample.Core");
        AssertEntry(outline[1], 1, "Class", "OrderService<T>");
        AssertEntry(outline[2], 2, "Field", "_repository");
        AssertEntry(outline[3], 2, "Method", "OrderService(");
        AssertEntry(outline[4], 2, "Property", "SavedCount");
        AssertEntry(outline[5], 2, "Method", "Validate");
        AssertEntry(outline[6], 2, "Method", "Save");
        foreach (var entry in outline)
            Assert.IsLessThanOrEqualTo(entry.EndLine, entry.StartLine, entry.Display);
        var lines = File.ReadAllLines(path);
        var lastCodeLine = Array.FindLastIndex(lines, line => !string.IsNullOrWhiteSpace(line)) + 1;
        Assert.AreEqual(lastCodeLine, outline[1].EndLine);
    }

    [TestMethod]
    public async Task GetDocumentOutlineAsync_FileNotInSolution_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), $"roslyn-mcp-tests-{Guid.NewGuid():N}.cs");
        File.WriteAllText(path, "public static class Outside { }\n");
        try
        {
            var ex = await Assert.ThrowsExactlyAsync<ArgumentException>(() => s_service.GetDocumentOutlineAsync(
                TestPaths.SampleSolutionPath, path, CancellationToken.None));

            StringAssert.Contains(ex.Message, path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void AssertEntry(OutlineEntry entry, int depth, string kind, string displayPart)
    {
        Assert.AreEqual(depth, entry.Depth, entry.Display);
        Assert.AreEqual(kind, entry.Kind, entry.Display);
        Assert.Contains(displayPart, entry.Display);
    }
}
