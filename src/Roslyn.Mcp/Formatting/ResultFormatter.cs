using Microsoft.CodeAnalysis;
using Roslyn.Mcp.Models;
using System.Text;
using SymbolInfo = Roslyn.Mcp.Models.SymbolInfo;

namespace Roslyn.Mcp.Formatting
{
    /// <summary>
    /// Turns the service result models into the markdown-ish text returned by the MCP tools.
    /// </summary>
    public static class ResultFormatter
    {
        public static string SearchResults(IReadOnlyList<SymbolSearchResult> results)
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

        public static string References(IReadOnlyList<ReferenceResult> results)
        {
            var output = new StringBuilder();
            output.AppendLine($"Found {results.Count} references:\n");

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

        public static string SymbolInfo(SymbolInfo? info)
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

        public static string Locations(List<SymbolLocationResult> results, string title)
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

        public static string Outline(string filePath, List<OutlineEntry> entries)
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

        public static string Diagnostics(List<DiagnosticResult> diagnostics, int maxResults)
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

        public static string WorkspaceSummary(WorkspaceSummary summary)
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

        public static string Dependencies(DependencyAnalysis analysis)
        {
            var output = new StringBuilder();
            output.AppendLine($"**Dependency Analysis for {analysis.SolutionName}**\n");

            foreach (var project in analysis.Projects)
            {
                var references = project.ProjectReferences.Count > 0
                    ? string.Join(", ", project.ProjectReferences)
                    : "none";
                output.AppendLine($"{project.Name} — {project.TotalSymbols} symbols " +
                                  $"({project.PublicSymbols} public, {project.InternalSymbols} internal); " +
                                  $"references: {references}");
            }

            output.AppendLine();
            output.AppendLine("Top namespaces:");
            if (analysis.NamespaceUsages.Count == 0)
                output.AppendLine("  (no using directives)");
            foreach (var usage in analysis.NamespaceUsages)
                output.AppendLine($"  • {usage.Namespace}: {usage.UsageCount}");

            return output.ToString();
        }

        public static string Complexity(List<ComplexityResult> results)
        {
            var output = new StringBuilder();
            output.AppendLine("**Code Complexity Analysis**\n");
            output.AppendLine($"Found {results.Count} methods with high complexity:\n");

            foreach (var result in results.OrderByDescending(r => r.Complexity).Take(20))
            {
                output.AppendLine($"**{result.ContainingType}.{result.MemberName}** (Complexity: {result.Complexity})");
                output.AppendLine($"  Location: {result.FilePath}:{result.LineNumber}");
                if (!string.IsNullOrEmpty(result.Namespace))
                    output.AppendLine($"  Namespace: {result.Namespace}");
                output.AppendLine();
            }

            if (results.Count > 20)
                output.AppendLine($"... and {results.Count - 20} more methods with high complexity");

            return output.ToString();
        }
    }
}
