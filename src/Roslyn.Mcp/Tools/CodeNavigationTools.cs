using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Roslyn.Mcp.Models;
using Roslyn.Mcp.Services;
using System.ComponentModel;
using System.Text;

namespace Roslyn.Mcp.Tools
{
    /// <summary>
    /// Read-only semantic code tools. Names use the "roslyn_" prefix so they sit cleanly next to
    /// the "dotnet_" tools of the .NET MCP server (https://github.com/jongalloway/dotnet-mcp),
    /// which covers the SDK/CLI side: creating projects, packages, build, test and run.
    /// </summary>
    [McpServerToolType]
    public class CodeNavigationTools
    {
        private const string SolutionPathDescription =
            "Absolute path to a .sln, .slnx or .csproj file, or to a directory containing exactly one of them " +
            "(the same solution or project you pass to dotnet_* tools)";

        private const string SymbolNameDescription =
            "Symbol name, optionally qualified to disambiguate (e.g. 'Save', 'OrderService.Save', 'MyApp.Services.OrderService')";

        [McpServerTool(Name = "roslyn_search_symbols", Title = "Search C# symbols",
            ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
        [Description("Search for symbols declared in source using wildcard patterns (* and ?). Returns file paths and line numbers.")]
        public static Task<string> SearchSymbols(
            SecurityValidator validator,
            SymbolSearchService searchService,
            ILogger<CodeNavigationTools> logger,
            [Description("Wildcard pattern to search for (e.g., 'User*', '*Service', 'Get*User')")] string pattern,
            [Description(SolutionPathDescription)] string solutionPath,
            [Description("Comma-separated symbol kinds: class, interface, struct, enum, record, delegate, method, property, field, event, namespace")] string symbolTypes = "class,interface,struct,enum,delegate,method,property",
            [Description("Whether to ignore case in search")] bool ignoreCase = true,
            CancellationToken cancellationToken = default)
        {
            var patternError = validator.ValidateSearchPattern(pattern);
            if (patternError != null)
                return Task.FromResult("Error: " + patternError);

            SymbolTypeFilter filter;
            try
            {
                filter = SymbolTypeFilter.Parse(symbolTypes);
            }
            catch (ArgumentException ex)
            {
                return Task.FromResult("Error: " + ex.Message);
            }

            return ExecuteAsync(validator, logger, solutionPath, "symbol search", async () =>
            {
                var results = await searchService.SearchSymbolsAsync(
                    pattern, solutionPath, filter, ignoreCase, cancellationToken);

                return FormatSearchResults(results);
            });
        }

        [McpServerTool(Name = "roslyn_find_references", Title = "Find references",
            ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
        [Description("Find all references to a symbol across the solution, with surrounding line text.")]
        public static Task<string> FindReferences(
            SecurityValidator validator,
            SymbolSearchService searchService,
            ILogger<CodeNavigationTools> logger,
            [Description(SymbolNameDescription)] string symbolName,
            [Description(SolutionPathDescription)] string solutionPath,
            [Description("Include symbol definition in results")] bool includeDefinition = true,
            CancellationToken cancellationToken = default)
        {
            return ExecuteAsync(validator, logger, solutionPath, "find references", async () =>
            {
                var results = await searchService.FindReferencesAsync(
                    symbolName, solutionPath, includeDefinition, cancellationToken);
                return FormatReferenceResults(results);
            });
        }

        [McpServerTool(Name = "roslyn_get_symbol_info", Title = "Get symbol info",
            ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
        [Description("Get detailed information about a symbol: kind, signature, accessibility, attributes and declaration location.")]
        public static Task<string> GetSymbolInfo(
            SecurityValidator validator,
            SymbolSearchService searchService,
            ILogger<CodeNavigationTools> logger,
            [Description(SymbolNameDescription)] string symbolName,
            [Description(SolutionPathDescription)] string solutionPath,
            CancellationToken cancellationToken = default)
        {
            return ExecuteAsync(validator, logger, solutionPath, "get symbol info", async () =>
            {
                var info = await searchService.GetSymbolInfoAsync(symbolName, solutionPath, cancellationToken);
                return FormatSymbolInfo(info);
            });
        }

        [McpServerTool(Name = "roslyn_find_implementations", Title = "Find implementations",
            ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
        [Description("Find implementations of an interface or interface member, classes derived from a class, " +
                     "and overrides of virtual/abstract members.")]
        public static Task<string> FindImplementations(
            SecurityValidator validator,
            SymbolSearchService searchService,
            ILogger<CodeNavigationTools> logger,
            [Description(SymbolNameDescription)] string symbolName,
            [Description(SolutionPathDescription)] string solutionPath,
            CancellationToken cancellationToken = default)
        {
            return ExecuteAsync(validator, logger, solutionPath, "find implementations", async () =>
            {
                var results = await searchService.FindImplementationsAsync(symbolName, solutionPath, cancellationToken);
                return FormatLocationResults(results, $"implementations of '{symbolName}'");
            });
        }

        [McpServerTool(Name = "roslyn_find_callers", Title = "Find callers",
            ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
        [Description("Find the methods, properties and constructors that call a method or access a property/event " +
                     "(one level of the call hierarchy).")]
        public static Task<string> FindCallers(
            SecurityValidator validator,
            SymbolSearchService searchService,
            ILogger<CodeNavigationTools> logger,
            [Description(SymbolNameDescription)] string symbolName,
            [Description(SolutionPathDescription)] string solutionPath,
            CancellationToken cancellationToken = default)
        {
            return ExecuteAsync(validator, logger, solutionPath, "find callers", async () =>
            {
                var results = await searchService.FindCallersAsync(symbolName, solutionPath, cancellationToken);
                return FormatLocationResults(results, $"callers of '{symbolName}'");
            });
        }

        [McpServerTool(Name = "roslyn_get_document_outline", Title = "Get file outline",
            ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
        [Description("List the namespaces, types and members declared in a C# file with signatures and line ranges, " +
                     "so only the relevant lines need to be read.")]
        public static Task<string> GetDocumentOutline(
            SecurityValidator validator,
            CodeAnalysisService analysisService,
            ILogger<CodeNavigationTools> logger,
            [Description("Absolute path to a .cs file in the solution")] string filePath,
            [Description(SolutionPathDescription)] string solutionPath,
            CancellationToken cancellationToken = default)
        {
            if (!validator.ValidateSourceFilePath(filePath))
                return Task.FromResult("Error: Invalid file path provided. Pass an absolute path to an existing .cs file.");

            return ExecuteAsync(validator, logger, solutionPath, "document outline", async () =>
            {
                var entries = await analysisService.GetDocumentOutlineAsync(solutionPath, filePath, cancellationToken);
                return FormatOutline(filePath, entries);
            });
        }

        [McpServerTool(Name = "roslyn_get_diagnostics", Title = "Get compiler diagnostics",
            ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
        [Description("Get C# compiler errors and warnings from the in-memory workspace without running a build. " +
                     "Fast feedback after editing a file; use dotnet_project Build for the authoritative result " +
                     "(MSBuild, analyzers, source generators). Many CS0246 'type not found' errors usually mean " +
                     "packages are not restored: run dotnet_project Restore.")]
        public static Task<string> GetDiagnostics(
            SecurityValidator validator,
            CodeAnalysisService analysisService,
            ILogger<CodeNavigationTools> logger,
            [Description(SolutionPathDescription)] string solutionPath,
            [Description("Optional absolute path to a single .cs file to check (much faster than the whole solution)")] string? filePath = null,
            [Description("Minimum severity: Error, Warning or Info")] string minimumSeverity = "Warning",
            [Description("Maximum number of diagnostics to return")] int maxResults = 100,
            CancellationToken cancellationToken = default)
        {
            if (!string.IsNullOrWhiteSpace(filePath) && !validator.ValidateSourceFilePath(filePath))
                return Task.FromResult("Error: Invalid file path provided. Pass an absolute path to an existing .cs file.");

            if (!Enum.TryParse<DiagnosticSeverity>(minimumSeverity, ignoreCase: true, out var severity))
                return Task.FromResult("Error: minimumSeverity must be Error, Warning or Info.");

            return ExecuteAsync(validator, logger, solutionPath, "get diagnostics", async () =>
            {
                var diagnostics = await analysisService.GetDiagnosticsAsync(solutionPath, filePath, severity, cancellationToken);
                return FormatDiagnostics(diagnostics, Math.Max(1, maxResults));
            });
        }

        [McpServerTool(Name = "roslyn_analyze_dependencies", Title = "Analyze dependencies",
            ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
        [Description("Analyze symbol counts, project/assembly references and namespace usage across the solution. " +
                     "For NuGet package versions and updates use dotnet_package List instead.")]
        public static Task<string> AnalyzeDependencies(
            SecurityValidator validator,
            CodeAnalysisService analysisService,
            ILogger<CodeNavigationTools> logger,
            [Description(SolutionPathDescription)] string solutionPath,
            [Description("Maximum depth for dependency analysis")] int maxDepth = 3,
            CancellationToken cancellationToken = default)
        {
            return ExecuteAsync(validator, logger, solutionPath, "dependency analysis", async () =>
            {
                var dependencies = await analysisService.AnalyzeDependenciesAsync(solutionPath, maxDepth, cancellationToken);
                return FormatDependencyAnalysis(dependencies);
            });
        }

        [McpServerTool(Name = "roslyn_analyze_code_complexity", Title = "Analyze code complexity",
            ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
        [Description("Find methods whose cyclomatic complexity is at or above a threshold.")]
        public static Task<string> AnalyzeCodeComplexity(
            SecurityValidator validator,
            WorkspaceManager workspaceManager,
            ILogger<CodeNavigationTools> logger,
            [Description(SolutionPathDescription)] string solutionPath,
            [Description("Complexity threshold (1-10)")] int threshold = 5,
            CancellationToken cancellationToken = default)
        {
            return ExecuteAsync(validator, logger, solutionPath, "complexity analysis", async () =>
            {
                var solution = await workspaceManager.GetSolutionAsync(solutionPath, cancellationToken);
                var complexityResults = new List<ComplexityResult>();

                foreach (var project in solution.Projects.Where(p => p.SupportsCompilation))
                {
                    var compilation = await project.GetCompilationAsync(cancellationToken);
                    if (compilation == null) continue;

                    foreach (var tree in compilation.SyntaxTrees)
                    {
                        var root = await tree.GetRootAsync(cancellationToken);
                        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>();

                        foreach (var method in methods)
                        {
                            var complexity = CalculateCyclomaticComplexity(method);
                            if (complexity >= threshold)
                            {
                                var lineSpan = method.GetLocation().GetLineSpan();
                                complexityResults.Add(new ComplexityResult
                                {
                                    MethodName = method.Identifier.ValueText,
                                    FileName = tree.FilePath,
                                    LineNumber = lineSpan.StartLinePosition.Line + 1,
                                    Complexity = complexity,
                                    ClassName = GetContainingClassName(method),
                                    Namespace = GetContainingNamespace(method)
                                });
                            }
                        }
                    }
                }

                // Multi-targeted projects contain the same syntax trees once per target framework.
                var distinct = complexityResults
                    .DistinctBy(r => (r.FileName, r.LineNumber))
                    .ToList();

                return FormatComplexityResults(distinct);
            });
        }

        [McpServerTool(Name = "roslyn_reload_workspace", Title = "Reload workspace",
            ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
        [Description("Discard the cached Roslyn workspace and load the solution again. The workspace already refreshes " +
                     "itself when files change on disk; use this if results still look stale, or to see MSBuild load warnings.")]
        public static Task<string> ReloadWorkspace(
            SecurityValidator validator,
            WorkspaceManager workspaceManager,
            ILogger<CodeNavigationTools> logger,
            [Description(SolutionPathDescription)] string solutionPath,
            CancellationToken cancellationToken = default)
        {
            return ExecuteAsync(validator, logger, solutionPath, "reload workspace", async () =>
            {
                var summary = await workspaceManager.ReloadAsync(solutionPath, cancellationToken);
                return FormatWorkspaceSummary(summary);
            });
        }

        private static async Task<string> ExecuteAsync(
            SecurityValidator validator,
            ILogger logger,
            string solutionPath,
            string operation,
            Func<Task<string>> action)
        {
            if (!validator.ValidateSolutionPath(solutionPath))
            {
                return "Error: Invalid solution path provided. Pass an absolute path to a .sln, .slnx or .csproj file, " +
                       "or to a directory that contains one.";
            }

            try
            {
                return await action();
            }
            catch (OperationCanceledException)
            {
                return $"Error: The {operation} operation was cancelled or timed out.";
            }
            catch (FileNotFoundException)
            {
                return "Error: Solution file not found. Please check the path and try again.";
            }
            catch (UnauthorizedAccessException)
            {
                return "Error: Access denied. Please check file permissions.";
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                // These carry messages written by this server (ambiguous solution, file not in solution, ...).
                return $"Error: {ex.Message}";
            }
            catch (Exception ex)
            {
                // Log full error details but return safe message
                logger.LogError(ex, "Unexpected error during {Operation}", operation);
                return $"Error: An unexpected error occurred during the {operation} operation.";
            }
        }

        private static string FormatSearchResults(IReadOnlyList<SymbolSearchResult> results)
        {
            var grouped = results.GroupBy(r => r.Category);
            var output = new StringBuilder();

            output.AppendLine($"Found {results.Count} symbols:\n");

            foreach (var group in grouped.OrderBy(g => g.Key))
            {
                output.AppendLine($"**{group.Key}** ({group.Count()}):");
                foreach (var result in group.Take(20)) // Limit results
                {
                    output.AppendLine($"  • `{result.FullName}` in {result.Location}");
                    if (!string.IsNullOrEmpty(result.Summary))
                        output.AppendLine($"    {result.Summary}");
                }
                if (group.Count() > 20)
                    output.AppendLine($"    ... and {group.Count() - 20} more");
                output.AppendLine();
            }

            return output.ToString();
        }

        private static string FormatReferenceResults(IEnumerable<ReferenceResult> results)
        {
            var output = new StringBuilder();
            output.AppendLine($"Found {results.Count()} references:\n");

            var groupedByFile = results.GroupBy(r => r.DocumentPath);

            foreach (var fileGroup in groupedByFile.OrderBy(g => g.Key))
            {
                output.AppendLine($"**{fileGroup.Key}** ({fileGroup.Count()} references):");

                foreach (var reference in fileGroup.OrderBy(r => r.LineNumber))
                {
                    var refType = reference.IsDefinition ? "Definition" : reference.ReferenceKind;
                    output.AppendLine($"  • Line {reference.LineNumber}: {refType}");
                    output.AppendLine($"    `{reference.LineText.Trim()}`");
                }
                output.AppendLine();
            }

            return output.ToString();
        }

        private static string FormatSymbolInfo(Roslyn.Mcp.Models.SymbolInfo? info)
        {
            if (info == null)
                return "Symbol not found.";

            var output = new StringBuilder();
            output.AppendLine($"**{info.Name}** ({info.Kind})");
            output.AppendLine($"Full Name: `{info.FullName}`");
            output.AppendLine($"Accessibility: {info.Accessibility}");

            if (!string.IsNullOrEmpty(info.Namespace))
                output.AppendLine($"Namespace: {info.Namespace}");

            if (!string.IsNullOrEmpty(info.DeclaringType))
                output.AppendLine($"Declaring Type: {info.DeclaringType}");

            if (!string.IsNullOrEmpty(info.ReturnType))
                output.AppendLine($"Return Type: {info.ReturnType}");

            if (info.Parameters.Count > 0)
            {
                output.AppendLine("Parameters:");
                foreach (var param in info.Parameters)
                    output.AppendLine($"  • {param}");
            }

            if (info.Attributes.Count > 0)
            {
                output.AppendLine("Attributes:");
                foreach (var attr in info.Attributes)
                    output.AppendLine($"  • {attr}");
            }

            if (!string.IsNullOrEmpty(info.SourceLocation))
                output.AppendLine($"Location: {info.SourceLocation}");

            if (info.OtherMatches.Count > 0)
            {
                output.AppendLine("Other symbols with this name (qualify the name to select one):");
                foreach (var match in info.OtherMatches)
                    output.AppendLine($"  • `{match}`");
            }

            return output.ToString();
        }

        private static string FormatLocationResults(List<SymbolLocationResult> results, string title)
        {
            if (results.Count == 0)
                return $"No {title} found.";

            var output = new StringBuilder();
            output.AppendLine($"Found {results.Count} {title}:\n");

            foreach (var fileGroup in results.GroupBy(r => r.FilePath))
            {
                output.AppendLine($"**{fileGroup.Key}**:");
                foreach (var result in fileGroup)
                {
                    output.AppendLine($"  • Line {result.LineNumber}: `{result.DisplayName}` ({result.Kind}, {result.Relation})");
                    if (!string.IsNullOrEmpty(result.LineText))
                        output.AppendLine($"    `{result.LineText}`");
                }
                output.AppendLine();
            }

            return output.ToString();
        }

        private static string FormatOutline(string filePath, List<OutlineEntry> entries)
        {
            if (entries.Count == 0)
                return $"No declarations found in {filePath}.";

            var output = new StringBuilder();
            output.AppendLine($"**Outline of {filePath}**\n");

            foreach (var entry in entries)
            {
                var indent = new string(' ', entry.Depth * 2);
                var lines = entry.StartLine == entry.EndLine
                    ? $"L{entry.StartLine}"
                    : $"L{entry.StartLine}-{entry.EndLine}";
                output.AppendLine($"{indent}- {lines} [{entry.Kind}] `{entry.Display}`");
            }

            return output.ToString();
        }

        private static string FormatDiagnostics(List<DiagnosticResult> diagnostics, int maxResults)
        {
            if (diagnostics.Count == 0)
                return "No diagnostics found.";

            var output = new StringBuilder();
            var errors = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
            var warnings = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);
            output.AppendLine($"Found {errors} errors, {warnings} warnings, {diagnostics.Count - errors - warnings} other:\n");

            foreach (var diagnostic in diagnostics.Take(maxResults))
            {
                var location = string.IsNullOrEmpty(diagnostic.FilePath)
                    ? $"[{diagnostic.ProjectName}]"
                    : $"{diagnostic.FilePath}({diagnostic.LineNumber},{diagnostic.ColumnNumber})";
                output.AppendLine($"{location}: {diagnostic.Severity.ToString().ToLowerInvariant()} {diagnostic.Id}: {diagnostic.Message}");
            }

            if (diagnostics.Count > maxResults)
                output.AppendLine($"\n... and {diagnostics.Count - maxResults} more (narrow with filePath or minimumSeverity)");

            return output.ToString();
        }

        private static string FormatWorkspaceSummary(WorkspaceSummary summary)
        {
            var output = new StringBuilder();
            output.AppendLine($"Reloaded {summary.SolutionPath}");
            output.AppendLine($"Projects: {summary.ProjectCount}, Documents: {summary.DocumentCount}");
            foreach (var project in summary.Projects)
                output.AppendLine($"  • {project}");

            if (summary.LoadWarnings.Count > 0)
            {
                output.AppendLine("\nMSBuild load warnings:");
                foreach (var warning in summary.LoadWarnings.Take(20))
                    output.AppendLine($"  • {warning}");
            }

            return output.ToString();
        }

        private static string FormatDependencyAnalysis(DependencyAnalysis analysis)
        {
            var output = new StringBuilder();
            output.AppendLine($"**Dependency Analysis for {analysis.ProjectName}**\n");

            output.AppendLine("Symbol Summary:");
            output.AppendLine($"  • Total Symbols: {analysis.TotalSymbols}");
            output.AppendLine($"  • Public Symbols: {analysis.PublicSymbols}");
            output.AppendLine($"  • Internal Symbols: {analysis.InternalSymbols}");
            output.AppendLine();

            if (analysis.Dependencies.Count > 0)
            {
                output.AppendLine("Dependencies:");
                var groupedDeps = analysis.Dependencies.GroupBy(d => d.Type);
                foreach (var group in groupedDeps)
                {
                    output.AppendLine($"  **{group.Key}** ({group.Count()}):");
                    foreach (var dep in group.Take(10))
                        output.AppendLine($"    • {dep.Name}");
                    if (group.Count() > 10)
                        output.AppendLine($"    ... and {group.Count() - 10} more");
                }
                output.AppendLine();
            }

            if (analysis.NamespaceUsages.Count > 0)
            {
                output.AppendLine("Top Namespace Usages:");
                foreach (var usage in analysis.NamespaceUsages.OrderByDescending(n => n.UsageCount).Take(10))
                    output.AppendLine($"  • {usage.Namespace}: {usage.UsageCount} usages");
            }

            return output.ToString();
        }

        private static string FormatComplexityResults(List<ComplexityResult> results)
        {
            var output = new StringBuilder();
            output.AppendLine("**Code Complexity Analysis**\n");
            output.AppendLine($"Found {results.Count} methods with high complexity:\n");

            foreach (var result in results.OrderByDescending(r => r.Complexity).Take(20))
            {
                output.AppendLine($"**{result.ClassName}.{result.MethodName}** (Complexity: {result.Complexity})");
                output.AppendLine($"  Location: {result.FileName}:{result.LineNumber}");
                if (!string.IsNullOrEmpty(result.Namespace))
                    output.AppendLine($"  Namespace: {result.Namespace}");
                output.AppendLine();
            }

            if (results.Count > 20)
                output.AppendLine($"... and {results.Count - 20} more methods with high complexity");

            return output.ToString();
        }

        private static int CalculateCyclomaticComplexity(MethodDeclarationSyntax method)
        {
            int complexity = 1; // Base complexity

            var decisionPoints = method.DescendantNodes().Where(node =>
                node.IsKind(SyntaxKind.IfStatement) ||
                node.IsKind(SyntaxKind.WhileStatement) ||
                node.IsKind(SyntaxKind.ForStatement) ||
                node.IsKind(SyntaxKind.ForEachStatement) ||
                node.IsKind(SyntaxKind.SwitchStatement) ||
                node.IsKind(SyntaxKind.CatchClause));

            complexity += decisionPoints.Count();

            // Add complexity for logical operators
            var logicalOperators = method.DescendantTokens().Where(token =>
                token.IsKind(SyntaxKind.AmpersandAmpersandToken) ||
                token.IsKind(SyntaxKind.BarBarToken));

            complexity += logicalOperators.Count();

            return complexity;
        }

        private static string GetContainingClassName(MethodDeclarationSyntax method)
        {
            var classDeclaration = method.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
            return classDeclaration?.Identifier.ValueText ?? "";
        }

        private static string GetContainingNamespace(MethodDeclarationSyntax method)
        {
            var namespaceDeclaration = method.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
            return namespaceDeclaration?.Name.ToString() ?? "";
        }
    }
}
