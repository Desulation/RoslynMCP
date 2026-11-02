using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Roslyn.Mcp.Formatting;
using Roslyn.Mcp.Services;
using System.ComponentModel;

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
            WorkspaceManager workspaces,
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

            return ExecuteAsync(validator, workspaces, logger, solutionPath, "symbol search", async () =>
            {
                var results = await searchService.SearchSymbolsAsync(
                    pattern, solutionPath, filter, ignoreCase, cancellationToken);

                return ResultFormatter.SearchResults(results);
            });
        }

        [McpServerTool(Name = "roslyn_find_references", Title = "Find references",
            ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
        [Description("Find all references to a symbol across the solution, with surrounding line text.")]
        public static Task<string> FindReferences(
            SecurityValidator validator,
            SymbolSearchService searchService,
            WorkspaceManager workspaces,
            ILogger<CodeNavigationTools> logger,
            [Description(SymbolNameDescription)] string symbolName,
            [Description(SolutionPathDescription)] string solutionPath,
            [Description("Include symbol definition in results")] bool includeDefinition = true,
            CancellationToken cancellationToken = default)
        {
            return ExecuteAsync(validator, workspaces, logger, solutionPath, "find references", async () =>
            {
                var results = await searchService.FindReferencesAsync(
                    symbolName, solutionPath, includeDefinition, cancellationToken);
                return ResultFormatter.References(results);
            });
        }

        [McpServerTool(Name = "roslyn_get_symbol_info", Title = "Get symbol info",
            ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
        [Description("Get detailed information about a symbol: kind, signature, accessibility, attributes and declaration location.")]
        public static Task<string> GetSymbolInfo(
            SecurityValidator validator,
            SymbolSearchService searchService,
            WorkspaceManager workspaces,
            ILogger<CodeNavigationTools> logger,
            [Description(SymbolNameDescription)] string symbolName,
            [Description(SolutionPathDescription)] string solutionPath,
            CancellationToken cancellationToken = default)
        {
            return ExecuteAsync(validator, workspaces, logger, solutionPath, "get symbol info", async () =>
            {
                var info = await searchService.GetSymbolInfoAsync(symbolName, solutionPath, cancellationToken);
                return ResultFormatter.SymbolInfo(info);
            });
        }

        [McpServerTool(Name = "roslyn_find_implementations", Title = "Find implementations",
            ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
        [Description("Find implementations of an interface or interface member, classes derived from a class, " +
                     "and overrides of virtual/abstract members.")]
        public static Task<string> FindImplementations(
            SecurityValidator validator,
            SymbolSearchService searchService,
            WorkspaceManager workspaces,
            ILogger<CodeNavigationTools> logger,
            [Description(SymbolNameDescription)] string symbolName,
            [Description(SolutionPathDescription)] string solutionPath,
            CancellationToken cancellationToken = default)
        {
            return ExecuteAsync(validator, workspaces, logger, solutionPath, "find implementations", async () =>
            {
                var results = await searchService.FindImplementationsAsync(symbolName, solutionPath, cancellationToken);
                return ResultFormatter.Locations(results, $"implementations of '{symbolName}'");
            });
        }

        [McpServerTool(Name = "roslyn_find_callers", Title = "Find callers",
            ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
        [Description("Find the methods, properties and constructors that call a method or access a property/event " +
                     "(one level of the call hierarchy).")]
        public static Task<string> FindCallers(
            SecurityValidator validator,
            SymbolSearchService searchService,
            WorkspaceManager workspaces,
            ILogger<CodeNavigationTools> logger,
            [Description(SymbolNameDescription)] string symbolName,
            [Description(SolutionPathDescription)] string solutionPath,
            CancellationToken cancellationToken = default)
        {
            return ExecuteAsync(validator, workspaces, logger, solutionPath, "find callers", async () =>
            {
                var results = await searchService.FindCallersAsync(symbolName, solutionPath, cancellationToken);
                return ResultFormatter.Locations(results, $"callers of '{symbolName}'");
            });
        }

        [McpServerTool(Name = "roslyn_get_document_outline", Title = "Get file outline",
            ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
        [Description("List the namespaces, types and members declared in a C# file with signatures and line ranges, " +
                     "so only the relevant lines need to be read.")]
        public static Task<string> GetDocumentOutline(
            SecurityValidator validator,
            CodeAnalysisService analysisService,
            WorkspaceManager workspaces,
            ILogger<CodeNavigationTools> logger,
            [Description("Absolute path to a .cs file in the solution")] string filePath,
            [Description(SolutionPathDescription)] string solutionPath,
            CancellationToken cancellationToken = default)
        {
            if (!validator.ValidateSourceFilePath(filePath))
                return Task.FromResult("Error: Invalid file path provided. Pass an absolute path to an existing .cs file.");

            return ExecuteAsync(validator, workspaces, logger, solutionPath, "document outline", async () =>
            {
                var entries = await analysisService.GetDocumentOutlineAsync(solutionPath, filePath, cancellationToken);
                return ResultFormatter.Outline(filePath, entries);
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
            WorkspaceManager workspaces,
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

            return ExecuteAsync(validator, workspaces, logger, solutionPath, "get diagnostics", async () =>
            {
                var diagnostics = await analysisService.GetDiagnosticsAsync(solutionPath, filePath, severity, cancellationToken);
                return ResultFormatter.Diagnostics(diagnostics, Math.Max(1, maxResults));
            });
        }

        [McpServerTool(Name = "roslyn_analyze_dependencies", Title = "Analyze dependencies",
            ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
        [Description("Report what Roslyn knows about the solution: per-project symbol counts (total/public/internal), " +
                     "project references as compiled, and the most used namespaces from using directives. " +
                     "For NuGet packages and versions use dotnet_package List; for MSBuild-level project info use dotnet_project Analyze.")]
        public static Task<string> AnalyzeDependencies(
            SecurityValidator validator,
            CodeAnalysisService analysisService,
            WorkspaceManager workspaces,
            ILogger<CodeNavigationTools> logger,
            [Description(SolutionPathDescription)] string solutionPath,
            [Description("Maximum number of namespaces to list, most used first")] int maxNamespaces = 20,
            CancellationToken cancellationToken = default)
        {
            return ExecuteAsync(validator, workspaces, logger, solutionPath, "dependency analysis", async () =>
            {
                var dependencies = await analysisService.AnalyzeDependenciesAsync(solutionPath, maxNamespaces, cancellationToken);
                return ResultFormatter.Dependencies(dependencies);
            });
        }

        [McpServerTool(Name = "roslyn_analyze_code_complexity", Title = "Analyze code complexity",
            ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
        [Description("Find members (methods, constructors, operators, accessors) whose cyclomatic complexity is at or above a threshold.")]
        public static Task<string> AnalyzeCodeComplexity(
            SecurityValidator validator,
            CodeAnalysisService analysisService,
            WorkspaceManager workspaces,
            ILogger<CodeNavigationTools> logger,
            [Description(SolutionPathDescription)] string solutionPath,
            [Description("Complexity threshold (1-10)")] int threshold = 5,
            CancellationToken cancellationToken = default)
        {
            return ExecuteAsync(validator, workspaces, logger, solutionPath, "complexity analysis", async () =>
            {
                var results = await analysisService.AnalyzeComplexityAsync(solutionPath, threshold, cancellationToken);
                return ResultFormatter.Complexity(results);
            });
        }

        [McpServerTool(Name = "roslyn_reload_workspace", Title = "Reload workspace",
            ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
        [Description("Discard the cached Roslyn workspace and load the solution again. The workspace already refreshes " +
                     "itself when files change on disk; use this if results still look stale, or to see MSBuild load warnings.")]
        public static Task<string> ReloadWorkspace(
            SecurityValidator validator,
            WorkspaceManager workspaces,
            ILogger<CodeNavigationTools> logger,
            [Description(SolutionPathDescription)] string solutionPath,
            CancellationToken cancellationToken = default)
        {
            return ExecuteAsync(validator, workspaces, logger, solutionPath, "reload workspace", async () =>
            {
                var summary = await workspaces.ReloadAsync(solutionPath, cancellationToken);

                // This result lists the warnings themselves, so the next tool call must not repeat them as a hint.
                TryTakeLoadHint(workspaces, logger, solutionPath, out _);
                return ResultFormatter.WorkspaceSummary(summary);
            }, appendLoadHint: false);
        }

        internal static async Task<string> ExecuteAsync(
            SecurityValidator validator,
            WorkspaceManager workspaces,
            ILogger logger,
            string solutionPath,
            string operation,
            Func<Task<string>> action,
            bool appendLoadHint = true)
        {
            if (!validator.ValidateSolutionPath(solutionPath))
            {
                return "Error: Invalid solution path provided. Pass an absolute path to a .sln, .slnx or .csproj file, " +
                       "or to a directory that contains one.";
            }

            try
            {
                var result = await action();

                // Only a successful result announces the load warnings, so an error never uses up the hint.
                if (appendLoadHint && TryTakeLoadHint(workspaces, logger, solutionPath, out var warningCount))
                {
                    result += $"\n(Workspace loaded with {warningCount} MSBuild warnings; " +
                              "call roslyn_reload_workspace to see them.)";
                }

                return result;
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

        // The hint is an extra; failing to look it up (for example because the solution path no longer
        // resolves) must not replace the result the action already produced.
        private static bool TryTakeLoadHint(WorkspaceManager workspaces, ILogger logger, string solutionPath, out int warningCount)
        {
            try
            {
                return workspaces.TryTakeLoadHint(solutionPath, out warningCount);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not look up the load hint for {SolutionPath}", solutionPath);
                warningCount = 0;
                return false;
            }
        }
    }
}
