using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using Roslyn.Mcp.Models;

namespace Roslyn.Mcp.Services
{
    public class CodeAnalysisService
    {
        private readonly ILogger<CodeAnalysisService> _logger;
        private readonly WorkspaceManager _workspaceManager;

        public CodeAnalysisService(ILogger<CodeAnalysisService> logger, WorkspaceManager workspaceManager)
        {
            _logger = logger;
            _workspaceManager = workspaceManager;
        }

        public async Task<List<DiagnosticResult>> GetDiagnosticsAsync(
            string solutionPath,
            string? filePath,
            DiagnosticSeverity minimumSeverity,
            CancellationToken cancellationToken = default
        )
        {
            var solution = await _workspaceManager.GetSolutionAsync(solutionPath, cancellationToken);
            var diagnostics = new List<(Diagnostic Diagnostic, string ProjectName)>();

            if (!string.IsNullOrWhiteSpace(filePath))
            {
                var documentIds = solution.GetDocumentIdsWithFilePath(Path.GetFullPath(filePath));
                if (documentIds.IsEmpty)
                    throw new ArgumentException($"'{filePath}' is not a document in the solution.");

                foreach (var documentId in documentIds)
                {
                    var document = solution.GetDocument(documentId)!;
                    var model = await document.GetSemanticModelAsync(cancellationToken);
                    if (model == null)
                        continue;

                    diagnostics.AddRange(model.GetDiagnostics(cancellationToken: cancellationToken)
                        .Select(d => (d, document.Project.Name)));
                }
            }
            else
            {
                foreach (var project in solution.Projects.Where(p => p.SupportsCompilation))
                {
                    var compilation = await project.GetCompilationAsync(cancellationToken);
                    if (compilation == null)
                        continue;

                    diagnostics.AddRange(compilation.GetDiagnostics(cancellationToken)
                        .Select(d => (d, project.Name)));
                }
            }

            // Multi-targeted projects report the same diagnostic once per target framework.
            var results = diagnostics
                .Where(entry => entry.Diagnostic.Severity >= minimumSeverity && !entry.Diagnostic.IsSuppressed)
                .Select(entry =>
                {
                    var lineSpan = entry.Diagnostic.Location.GetLineSpan();
                    return new DiagnosticResult
                    {
                        Id = entry.Diagnostic.Id,
                        Severity = entry.Diagnostic.Severity,
                        Message = entry.Diagnostic.GetMessage(),
                        ProjectName = entry.ProjectName,
                        FilePath = lineSpan.Path ?? string.Empty,
                        LineNumber = lineSpan.StartLinePosition.Line + 1,
                        ColumnNumber = lineSpan.StartLinePosition.Character + 1,
                    };
                })
                .DistinctBy(d => (d.Id, d.FilePath, d.LineNumber, d.ColumnNumber, d.Message))
                .OrderByDescending(d => d.Severity)
                .ThenBy(d => d.FilePath)
                .ThenBy(d => d.LineNumber)
                .ToList();

            var notFound = results.Where(d => d.Id is "CS0246" or "CS0234").ToList();
            if (notFound.Count > RestoreHintThreshold)
            {
                results.Insert(0, new DiagnosticResult
                {
                    Id = RestoreHintId,
                    Severity = DiagnosticSeverity.Error,
                    Message = RestoreHintMessage,
                    ProjectName = notFound[0].ProjectName,
                });
            }

            return results;
        }

        private const int RestoreHintThreshold = 10;
        private const string RestoreHintId = "RMCP001";
        private const string RestoreHintMessage =
            "Many 'type or namespace not found' errors: packages are probably not restored. Run dotnet_project Restore.";

        private static readonly SymbolDisplayFormat OutlineFormat = new(
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            memberOptions: SymbolDisplayMemberOptions.IncludeAccessibility |
                           SymbolDisplayMemberOptions.IncludeModifiers |
                           SymbolDisplayMemberOptions.IncludeType |
                           SymbolDisplayMemberOptions.IncludeParameters |
                           SymbolDisplayMemberOptions.IncludeConstantValue,
            delegateStyle: SymbolDisplayDelegateStyle.NameAndSignature,
            propertyStyle: SymbolDisplayPropertyStyle.ShowReadWriteDescriptor,
            parameterOptions: SymbolDisplayParameterOptions.IncludeType |
                              SymbolDisplayParameterOptions.IncludeName |
                              SymbolDisplayParameterOptions.IncludeParamsRefOut |
                              SymbolDisplayParameterOptions.IncludeDefaultValue,
            kindOptions: SymbolDisplayKindOptions.IncludeTypeKeyword,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
                                  SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
                                  SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

        public async Task<List<OutlineEntry>> GetDocumentOutlineAsync(
            string solutionPath,
            string filePath,
            CancellationToken cancellationToken = default
        )
        {
            var solution = await _workspaceManager.GetSolutionAsync(solutionPath, cancellationToken);
            var documentId = solution.GetDocumentIdsWithFilePath(Path.GetFullPath(filePath)).FirstOrDefault()
                ?? throw new ArgumentException($"'{filePath}' is not a document in the solution.");

            var document = solution.GetDocument(documentId)!;
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            var model = await document.GetSemanticModelAsync(cancellationToken);
            var entries = new List<OutlineEntry>();
            if (root == null || model == null)
                return entries;

            void Add(SyntaxNode node, int depth, string kind, string display)
            {
                var lineSpan = node.GetLocation().GetLineSpan();
                entries.Add(new OutlineEntry
                {
                    Depth = depth,
                    Kind = kind,
                    Display = display,
                    StartLine = lineSpan.StartLinePosition.Line + 1,
                    EndLine = lineSpan.EndLinePosition.Line + 1,
                });
            }

            void AddSymbol(SyntaxNode node, int depth)
            {
                var symbol = model.GetDeclaredSymbol(node, cancellationToken);
                if (symbol == null)
                    return;

                var kind = symbol is INamedTypeSymbol type ? type.TypeKind.ToString() : symbol.Kind.ToString();
                Add(node, depth, kind, symbol.ToDisplayString(OutlineFormat));
            }

            void Walk(SyntaxNode parent, int depth)
            {
                foreach (var child in parent.ChildNodes())
                {
                    switch (child)
                    {
                        case BaseNamespaceDeclarationSyntax ns:
                            Add(ns, depth, "Namespace", ns.Name.ToString());
                            Walk(ns, depth + 1);
                            break;
                        case BaseTypeDeclarationSyntax:
                            AddSymbol(child, depth);
                            Walk(child, depth + 1);
                            break;
                        case BaseFieldDeclarationSyntax field:
                            foreach (var variable in field.Declaration.Variables)
                                AddSymbol(variable, depth);
                            break;
                        case MemberDeclarationSyntax:
                            AddSymbol(child, depth);
                            break;
                    }
                }
            }

            Walk(root, 0);
            return entries;
        }

        public async Task<DependencyAnalysis> AnalyzeDependenciesAsync(
            string solutionPath,
            int maxNamespaces,
            CancellationToken cancellationToken = default
        )
        {
            var solution = await _workspaceManager.GetSolutionAsync(solutionPath, cancellationToken);
            var projects = new List<ProjectSummary>();
            var namespaceCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            // Multi-targeted projects contain the same documents once per target framework;
            // each file's using directives are counted once.
            var countedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var project in solution.Projects.Where(project => project.SupportsCompilation))
            {
                var summary = new ProjectSummary
                {
                    Name = project.Name,
                    ProjectReferences = project.ProjectReferences
                        .Select(reference => solution.GetProject(reference.ProjectId)?.Name)
                        .OfType<string>()
                        .OrderBy(name => name, StringComparer.Ordinal)
                        .ToList(),
                };

                var compilation = await project.GetCompilationAsync(cancellationToken);
                if (compilation != null)
                {
                    foreach (var symbol in GetDeclaredSymbols(compilation.Assembly.GlobalNamespace))
                    {
                        summary.TotalSymbols++;
                        if (symbol.DeclaredAccessibility == Accessibility.Public)
                            summary.PublicSymbols++;
                        else if (symbol.DeclaredAccessibility == Accessibility.Internal)
                            summary.InternalSymbols++;
                    }
                }

                projects.Add(summary);

                foreach (var document in project.Documents)
                {
                    if (document.FilePath != null && !countedFiles.Add(document.FilePath))
                        continue;

                    var root = await document.GetSyntaxRootAsync(cancellationToken);
                    if (root == null || IsAutoGenerated(root))
                        continue;

                    foreach (var usingDirective in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
                    {
                        var namespaceName = usingDirective.Name?.ToString();
                        if (!string.IsNullOrWhiteSpace(namespaceName))
                            namespaceCounts[namespaceName] = namespaceCounts.GetValueOrDefault(namespaceName) + 1;
                    }
                }
            }

            return new DependencyAnalysis
            {
                SolutionName = Path.GetFileNameWithoutExtension(solutionPath),
                Projects = projects.OrderBy(project => project.Name, StringComparer.Ordinal).ToList(),
                NamespaceUsages = namespaceCounts
                    .OrderByDescending(entry => entry.Value)
                    .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                    .Take(Math.Max(1, maxNamespaces))
                    .Select(entry => new NamespaceUsage { Namespace = entry.Key, UsageCount = entry.Value })
                    .ToList(),
            };
        }

        // MSBuild writes AssemblyInfo/AssemblyAttributes/GlobalUsings files into obj/; their using
        // directives say nothing about the code, so files marked auto-generated are not counted.
        private static bool IsAutoGenerated(SyntaxNode root)
        {
            return root.GetLeadingTrivia()
                .Where(trivia => trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
                                 trivia.IsKind(SyntaxKind.MultiLineCommentTrivia))
                .Select(trivia => trivia.ToString())
                .Any(comment => comment.Contains("<auto-generated", StringComparison.OrdinalIgnoreCase) ||
                                comment.Contains("<autogenerated", StringComparison.OrdinalIgnoreCase));
        }

        private static readonly SymbolDisplayFormat ComplexityMemberFormat = new(
            memberOptions: SymbolDisplayMemberOptions.IncludeParameters,
            parameterOptions: SymbolDisplayParameterOptions.IncludeType,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

        public async Task<List<ComplexityResult>> AnalyzeComplexityAsync(
            string solutionPath,
            int threshold,
            CancellationToken cancellationToken = default
        )
        {
            var solution = await _workspaceManager.GetSolutionAsync(solutionPath, cancellationToken);
            var results = new List<ComplexityResult>();

            foreach (var project in solution.Projects.Where(project => project.SupportsCompilation))
            {
                foreach (var document in project.Documents)
                {
                    var root = await document.GetSyntaxRootAsync(cancellationToken);
                    var model = await document.GetSemanticModelAsync(cancellationToken);
                    if (root == null || model == null)
                        continue;

                    foreach (var member in root.DescendantNodes().Where(ComplexityCalculator.IsMemberLike))
                    {
                        var complexity = ComplexityCalculator.Calculate(member);
                        if (complexity < threshold)
                            continue;

                        // An expression-bodied property or indexer is reported as its getter, like accessor results.
                        var symbol = model.GetDeclaredSymbol(member, cancellationToken) switch
                        {
                            IPropertySymbol property => property.GetMethod,
                            var declared => declared,
                        };
                        var containingNamespace = symbol?.ContainingNamespace;
                        results.Add(new ComplexityResult
                        {
                            MemberName = symbol?.ToDisplayString(ComplexityMemberFormat) ?? string.Empty,
                            ContainingType = symbol?.ContainingType?.Name ?? string.Empty,
                            Namespace = containingNamespace == null || containingNamespace.IsGlobalNamespace
                                ? string.Empty
                                : containingNamespace.ToDisplayString(),
                            FilePath = document.FilePath ?? member.SyntaxTree.FilePath,
                            LineNumber = member.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                            Complexity = complexity,
                        });
                    }
                }
            }

            // Multi-targeted projects contain the same documents once per target framework.
            return results
                .DistinctBy(r => (r.FilePath, r.LineNumber))
                .OrderByDescending(r => r.Complexity)
                .ThenBy(r => r.FilePath, StringComparer.Ordinal)
                .ThenBy(r => r.LineNumber)
                .ToList();
        }

        // Types and members declared in source: namespaces are walked but not counted, and
        // compiler-generated members and property/event accessors are skipped (as in SymbolResolver).
        private static IEnumerable<ISymbol> GetDeclaredSymbols(INamespaceOrTypeSymbol container)
        {
            foreach (var member in container.GetMembers())
            {
                if (member.IsImplicitlyDeclared || member is IMethodSymbol { AssociatedSymbol: not null })
                    continue;

                if (member is not INamespaceSymbol)
                    yield return member;

                if (member is INamespaceOrTypeSymbol nestedContainer)
                {
                    foreach (var nestedMember in GetDeclaredSymbols(nestedContainer))
                        yield return nestedMember;
                }
            }
        }
    }
}
