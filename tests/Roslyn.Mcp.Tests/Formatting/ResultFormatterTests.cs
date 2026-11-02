using Microsoft.CodeAnalysis;
using Roslyn.Mcp.Formatting;
using Roslyn.Mcp.Models;
using SymbolInfo = Roslyn.Mcp.Models.SymbolInfo;

namespace Roslyn.Mcp.Tests.Formatting;

[TestClass]
public sealed class ResultFormatterTests
{
    [TestMethod]
    public void SearchResults_Empty_SaysFoundZero()
    {
        var output = ResultFormatter.SearchResults([]);

        CollectionAssert.AreEqual(new[] { "Found 0 symbols:", "", "" }, Lines(output));
    }

    [TestMethod]
    public void SearchResults_GroupsByCategory_Sorted()
    {
        var results = new List<SymbolSearchResult>
        {
            new() { Category = "Method", FullName = "Sample.OrderService.Save", Location = "OrderService.cs:12" },
            new() { Category = "Class", FullName = "Sample.OrderService", Location = "OrderService.cs:5", Summary = "Saves orders." },
            new() { Category = "Class", FullName = "Sample.Order", Location = "Order.cs:3" },
        };

        var output = ResultFormatter.SearchResults(results);

        CollectionAssert.AreEqual(
            new[]
            {
                "Found 3 symbols:",
                "",
                "**Class** (2):",
                "  • `Sample.OrderService` in OrderService.cs:5",
                "    Saves orders.",
                "  • `Sample.Order` in Order.cs:3",
                "",
                "**Method** (1):",
                "  • `Sample.OrderService.Save` in OrderService.cs:12",
                "",
                "",
            },
            Lines(output));
    }

    [TestMethod]
    public void SearchResults_MoreThanTwentyInCategory_AppendsAndNMore()
    {
        var results = Enumerable.Range(1, 25)
            .Select(i => new SymbolSearchResult { Category = "Class", FullName = $"Sample.C{i}", Location = $"C{i}.cs:1" })
            .ToList();

        var lines = Lines(ResultFormatter.SearchResults(results));

        Assert.AreEqual("**Class** (25):", lines[2]);
        Assert.AreEqual(20, lines.Count(l => l.StartsWith("  • ", StringComparison.Ordinal)));
        Assert.AreEqual("  • `Sample.C20` in C20.cs:1", lines[22]);
        Assert.AreEqual("    ... and 5 more", lines[23]);
    }

    [TestMethod]
    public void References_DefinitionMarked()
    {
        var results = new List<ReferenceResult>
        {
            new() { DocumentPath = "Order.cs", LineNumber = 3, LineText = "  public class Order  ", IsDefinition = true, ReferenceKind = "Reference" },
            new() { DocumentPath = "Order.cs", LineNumber = 8, LineText = "var o = new Order();", ReferenceKind = "Reference" },
        };

        var output = ResultFormatter.References(results);

        CollectionAssert.AreEqual(
            new[]
            {
                "Found 2 references:",
                "",
                "**Order.cs** (2 references):",
                "  • Line 3: Definition",
                "    `public class Order`",
                "  • Line 8: Reference",
                "    `var o = new Order();`",
                "",
                "",
            },
            Lines(output));
    }

    [TestMethod]
    public void References_GroupedByFileAndSortedByLine()
    {
        var results = new List<ReferenceResult>
        {
            new() { DocumentPath = "b.cs", LineNumber = 9, LineText = "b9", ReferenceKind = "Reference" },
            new() { DocumentPath = "a.cs", LineNumber = 7, LineText = "a7", ReferenceKind = "Reference" },
            new() { DocumentPath = "a.cs", LineNumber = 2, LineText = "a2", ReferenceKind = "Reference" },
        };

        var output = ResultFormatter.References(results);

        CollectionAssert.AreEqual(
            new[]
            {
                "Found 3 references:",
                "",
                "**a.cs** (2 references):",
                "  • Line 2: Reference",
                "    `a2`",
                "  • Line 7: Reference",
                "    `a7`",
                "",
                "**b.cs** (1 references):",
                "  • Line 9: Reference",
                "    `b9`",
                "",
                "",
            },
            Lines(output));
    }

    [TestMethod]
    public void SymbolInfo_Null_SaysNotFound()
    {
        Assert.AreEqual("Symbol not found.", ResultFormatter.SymbolInfo(null));
    }

    [TestMethod]
    public void SymbolInfo_OtherMatches_ListsThem()
    {
        var info = new SymbolInfo
        {
            Name = "Save",
            Kind = "Method",
            FullName = "Sample.OrderService.Save(Order)",
            Accessibility = "Public",
            Namespace = "Sample",
            DeclaringType = "OrderService",
            ReturnType = "void",
            Parameters = ["Order order"],
            SourceLocation = "OrderService.cs:12",
            OtherMatches = ["Sample.Repository.Save(Order)", "Sample.Cache.Save()"],
        };

        var output = ResultFormatter.SymbolInfo(info);

        CollectionAssert.AreEqual(
            new[]
            {
                "**Save** (Method)",
                "Full Name: `Sample.OrderService.Save(Order)`",
                "Accessibility: Public",
                "Namespace: Sample",
                "Declaring Type: OrderService",
                "Return Type: void",
                "Parameters:",
                "  • Order order",
                "Location: OrderService.cs:12",
                "Other symbols with this name (qualify the name to select one):",
                "  • `Sample.Repository.Save(Order)`",
                "  • `Sample.Cache.Save()`",
                "",
            },
            Lines(output));
    }

    [TestMethod]
    public void Locations_Empty_SaysNoneFound()
    {
        Assert.AreEqual("No callers of 'X' found.", ResultFormatter.Locations([], "callers of 'X'"));
    }

    [TestMethod]
    public void Locations_GroupsByFile()
    {
        var results = new List<SymbolLocationResult>
        {
            new() { FilePath = "App.cs", LineNumber = 4, DisplayName = "Program.Main()", Kind = "Method", Relation = "calls", LineText = "service.Save(order);" },
            new() { FilePath = "App.cs", LineNumber = 9, DisplayName = "Program.Run()", Kind = "Method", Relation = "calls" },
        };

        var output = ResultFormatter.Locations(results, "callers of 'Save'");

        CollectionAssert.AreEqual(
            new[]
            {
                "Found 2 callers of 'Save':",
                "",
                "**App.cs**:",
                "  • Line 4: `Program.Main()` (Method, calls)",
                "    `service.Save(order);`",
                "  • Line 9: `Program.Run()` (Method, calls)",
                "",
                "",
            },
            Lines(output));
    }

    [TestMethod]
    public void Outline_IndentsByDepthAndShowsRanges()
    {
        var entries = new List<OutlineEntry>
        {
            new() { Depth = 0, Kind = "Namespace", Display = "Sample", StartLine = 1, EndLine = 10 },
            new() { Depth = 1, Kind = "Field", Display = "int _count", StartLine = 3, EndLine = 3 },
            new() { Depth = 1, Kind = "Method", Display = "void Save()", StartLine = 5, EndLine = 9 },
        };

        var output = ResultFormatter.Outline("C:/src/Order.cs", entries);

        CollectionAssert.AreEqual(
            new[]
            {
                "**Outline of C:/src/Order.cs**",
                "",
                "- L1-10 [Namespace] `Sample`",
                "  - L3 [Field] `int _count`",
                "  - L5-9 [Method] `void Save()`",
                "",
            },
            Lines(output));
    }

    [TestMethod]
    public void Outline_Empty_SaysNoDeclarations()
    {
        Assert.AreEqual("No declarations found in C:/src/Empty.cs.", ResultFormatter.Outline("C:/src/Empty.cs", []));
    }

    [TestMethod]
    public void Diagnostics_CountsBySeverityAndCaps()
    {
        var diagnostics = new List<DiagnosticResult>
        {
            new() { Id = "CS0103", Severity = DiagnosticSeverity.Error, Message = "Name 'x' does not exist", ProjectName = "Sample.Core", FilePath = "Order.cs", LineNumber = 4, ColumnNumber = 9 },
            new() { Id = "CS8019", Severity = DiagnosticSeverity.Error, Message = "Project-level error", ProjectName = "Sample.Core" },
            new() { Id = "CS0246", Severity = DiagnosticSeverity.Error, Message = "Type not found", ProjectName = "Sample.Core", FilePath = "Order.cs", LineNumber = 6, ColumnNumber = 1 },
            new() { Id = "CS0219", Severity = DiagnosticSeverity.Warning, Message = "Assigned but never used", ProjectName = "Sample.Core", FilePath = "Order.cs", LineNumber = 7, ColumnNumber = 13 },
            new() { Id = "CS0168", Severity = DiagnosticSeverity.Warning, Message = "Declared but never used", ProjectName = "Sample.Core", FilePath = "Order.cs", LineNumber = 8, ColumnNumber = 13 },
        };

        var output = ResultFormatter.Diagnostics(diagnostics, maxResults: 2);

        CollectionAssert.AreEqual(
            new[]
            {
                "Found 3 errors, 2 warnings, 0 other:",
                "",
                "Order.cs(4,9): error CS0103: Name 'x' does not exist",
                "[Sample.Core]: error CS8019: Project-level error",
                "",
                "... and 3 more (narrow with filePath or minimumSeverity)",
                "",
            },
            Lines(output));
    }

    [TestMethod]
    public void Diagnostics_Empty_SaysNoneFound()
    {
        Assert.AreEqual("No diagnostics found.", ResultFormatter.Diagnostics([], maxResults: 100));
    }

    [TestMethod]
    public void WorkspaceSummary_ListsWarnings()
    {
        var summary = new WorkspaceSummary
        {
            SolutionPath = "C:/src/Sample.slnx",
            ProjectCount = 2,
            DocumentCount = 7,
            Projects = ["Sample.Core", "Sample.App"],
            LoadWarnings = ["Msbuild failed to load Legacy.csproj"],
        };

        var output = ResultFormatter.WorkspaceSummary(summary);

        CollectionAssert.AreEqual(
            new[]
            {
                "Reloaded C:/src/Sample.slnx",
                "Projects: 2, Documents: 7",
                "  • Sample.Core",
                "  • Sample.App",
                "",
                "MSBuild load warnings:",
                "  • Msbuild failed to load Legacy.csproj",
                "",
            },
            Lines(output));
    }

    [TestMethod]
    public void WorkspaceSummary_MoreThan20Warnings_CappedWithMoreLine()
    {
        var summary = new WorkspaceSummary
        {
            SolutionPath = "C:/src/Sample.slnx",
            ProjectCount = 1,
            DocumentCount = 1,
            Projects = ["Sample.Core"],
            LoadWarnings = Enumerable.Range(1, 23).Select(i => $"warning {i}").ToList(),
        };

        var lines = Lines(ResultFormatter.WorkspaceSummary(summary));

        Assert.AreEqual(20, lines.Count(l => l.StartsWith("  • warning ", StringComparison.Ordinal)));
        Assert.AreEqual("  • warning 20", lines.Last(l => l.StartsWith("  • warning ", StringComparison.Ordinal)));
        Assert.AreEqual("... and 3 more MSBuild load warnings", lines[^2]);
    }

    [TestMethod]
    public void Dependencies_PrintsProjectsThenNamespaces()
    {
        var analysis = new DependencyAnalysis
        {
            SolutionName = "Sample",
            Projects =
            [
                new() { Name = "Sample.Core", TotalSymbols = 10, PublicSymbols = 6, InternalSymbols = 4 },
                new() { Name = "Sample.App", TotalSymbols = 3, PublicSymbols = 1, InternalSymbols = 2, ProjectReferences = ["Sample.Core"] },
            ],
            NamespaceUsages =
            [
                new() { Namespace = "System", UsageCount = 5 },
                new() { Namespace = "Sample.Core", UsageCount = 2 },
            ],
        };

        var output = ResultFormatter.Dependencies(analysis);

        CollectionAssert.AreEqual(
            new[]
            {
                "**Dependency Analysis for Sample**",
                "",
                "Sample.Core — 10 symbols (6 public, 4 internal); references: none",
                "Sample.App — 3 symbols (1 public, 2 internal); references: Sample.Core",
                "",
                "Top namespaces:",
                "  • System: 5",
                "  • Sample.Core: 2",
                "",
            },
            Lines(output));
    }

    [TestMethod]
    public void Dependencies_NoNamespaces_SaysNoUsingDirectives()
    {
        var output = ResultFormatter.Dependencies(new DependencyAnalysis { SolutionName = "Empty" });

        CollectionAssert.AreEqual(
            new[] { "**Dependency Analysis for Empty**", "", "", "Top namespaces:", "  (no using directives)", "" },
            Lines(output));
    }

    [TestMethod]
    public void Complexity_SortedDescendingAndCapped()
    {
        var results = Enumerable.Range(1, 22)
            .Select(i => new ComplexityResult
            {
                ContainingType = "OrderService",
                MemberName = $"M{i}",
                Namespace = i == 22 ? "Sample" : string.Empty,
                FilePath = "OrderService.cs",
                LineNumber = i,
                Complexity = i,
            })
            .ToList();

        var lines = Lines(ResultFormatter.Complexity(results));

        CollectionAssert.AreEqual(
            new[]
            {
                "**Code Complexity Analysis**",
                "",
                "Found 22 members with high complexity:",
                "",
                "**OrderService.M22** (Complexity: 22)",
                "  Location: OrderService.cs:22",
                "  Namespace: Sample",
                "",
                "**OrderService.M21** (Complexity: 21)",
                "  Location: OrderService.cs:21",
                "",
            },
            lines.Take(11).ToArray());
        Assert.AreEqual(20, lines.Count(l => l.StartsWith("**OrderService.", StringComparison.Ordinal)));
        Assert.AreEqual("**OrderService.M3** (Complexity: 3)", lines.Last(l => l.StartsWith("**OrderService.", StringComparison.Ordinal)));
        Assert.AreEqual("... and 2 more members with high complexity", lines[^2]);
    }

    /// <summary>Splits on line breaks, accepting both the embedded "\n" and Environment.NewLine.</summary>
    private static string[] Lines(string output) => output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
}
