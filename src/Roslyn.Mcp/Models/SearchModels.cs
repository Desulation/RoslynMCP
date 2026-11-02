using Microsoft.CodeAnalysis;

namespace Roslyn.Mcp.Models
{
    public class SymbolSearchResult
    {
        public string Name { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Location { get; set; } = string.Empty;
        public string ProjectName { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public int LineNumber { get; set; }
        public string Summary { get; set; } = string.Empty;
        public string Accessibility { get; set; } = string.Empty;
        public SymbolKind SymbolKind { get; set; }
        public string Namespace { get; set; } = string.Empty;
    }

    public class ReferenceResult
    {
        public string SymbolName { get; set; } = string.Empty;
        public string DocumentPath { get; set; } = string.Empty;
        public string ProjectName { get; set; } = string.Empty;
        public int LineNumber { get; set; }
        public int ColumnNumber { get; set; }
        public string LineText { get; set; } = string.Empty;
        public List<string> Context { get; set; } = new();
        public bool IsDefinition { get; set; }
        public string ReferenceKind { get; set; } = string.Empty;
    }

    public class SymbolInfo
    {
        public string Name { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string Accessibility { get; set; } = string.Empty;
        public string DeclaringType { get; set; } = string.Empty;
        public string Namespace { get; set; } = string.Empty;
        public string Assembly { get; set; } = string.Empty;
        public string Documentation { get; set; } = string.Empty;
        public List<string> Parameters { get; set; } = new();
        public string ReturnType { get; set; } = string.Empty;
        public List<string> Attributes { get; set; } = new();
        public string SourceLocation { get; set; } = string.Empty;
        public List<string> OtherMatches { get; set; } = new();
    }

    public sealed class DependencyAnalysis
    {
        public string SolutionName { get; set; } = string.Empty;
        public List<ProjectSummary> Projects { get; set; } = new();
        public List<NamespaceUsage> NamespaceUsages { get; set; } = new();
    }

    public sealed class ProjectSummary
    {
        public string Name { get; set; } = string.Empty;
        public int TotalSymbols { get; set; }
        public int PublicSymbols { get; set; }
        public int InternalSymbols { get; set; }
        public List<string> ProjectReferences { get; set; } = new();
    }

    public sealed class NamespaceUsage
    {
        public string Namespace { get; set; } = string.Empty;
        public int UsageCount { get; set; }
    }

    public class ComplexityResult
    {
        public string MemberName { get; set; } = string.Empty;
        public string ContainingType { get; set; } = string.Empty;
        public string Namespace { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public int LineNumber { get; set; }
        public int Complexity { get; set; }
    }

    public class SymbolLocationResult
    {
        public string DisplayName { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string Relation { get; set; } = string.Empty;
        public string ProjectName { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public int LineNumber { get; set; }
        public string LineText { get; set; } = string.Empty;
    }

    public class DiagnosticResult
    {
        public string Id { get; set; } = string.Empty;
        public DiagnosticSeverity Severity { get; set; }
        public string Message { get; set; } = string.Empty;
        public string ProjectName { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public int LineNumber { get; set; }
        public int ColumnNumber { get; set; }
    }

    public class OutlineEntry
    {
        public int Depth { get; set; }
        public string Kind { get; set; } = string.Empty;
        public string Display { get; set; } = string.Empty;
        public int StartLine { get; set; }
        public int EndLine { get; set; }
    }

    public class WorkspaceSummary
    {
        public string SolutionPath { get; set; } = string.Empty;
        public int ProjectCount { get; set; }
        public int DocumentCount { get; set; }
        public List<string> Projects { get; set; } = new();
        public List<string> LoadWarnings { get; set; } = new();
    }
}