using Microsoft.CodeAnalysis;
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
            return diagnostics
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
        }

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
            int maxDepth,
            CancellationToken cancellationToken = default
        )
        {
            var solution = await _workspaceManager.GetSolutionAsync(solutionPath, cancellationToken);
            var analysis = new DependencyAnalysis
            {
                ProjectName = Path.GetFileNameWithoutExtension(solutionPath),
            };
            var namespaceUsages = new Dictionary<string, NamespaceUsage>(StringComparer.Ordinal);
            var dependencies = new Dictionary<string, ProjectDependency>(StringComparer.Ordinal);

            foreach (var project in solution.Projects.Where(project => project.SupportsCompilation))
            {
                foreach (var projectReference in project.ProjectReferences)
                {
                    var referencedProject = solution.GetProject(projectReference.ProjectId);
                    AddDependency(
                        dependencies,
                        referencedProject?.Name ?? projectReference.ProjectId.Id.ToString(),
                        "ProjectReference"
                    );
                }

                foreach (var metadataReference in project.MetadataReferences)
                {
                    if (!string.IsNullOrEmpty(metadataReference.Display))
                    {
                        AddDependency(
                            dependencies,
                            Path.GetFileNameWithoutExtension(metadataReference.Display),
                            "MetadataReference"
                        );
                    }
                }

                var compilation = await project.GetCompilationAsync(cancellationToken);
                if (compilation != null)
                {
                    var symbols = GetSymbols(compilation.Assembly.GlobalNamespace).ToList();
                    analysis.TotalSymbols += symbols.Count;
                    analysis.PublicSymbols += symbols.Count(symbol =>
                        symbol.DeclaredAccessibility == Accessibility.Public
                    );
                    analysis.InternalSymbols += symbols.Count(symbol =>
                        symbol.DeclaredAccessibility == Accessibility.Internal
                    );
                }

                foreach (var document in project.Documents)
                {
                    var root = await document.GetSyntaxRootAsync(cancellationToken);
                    if (root == null)
                    {
                        continue;
                    }

                    foreach (
                        var usingDirective in root.DescendantNodes().OfType<UsingDirectiveSyntax>()
                    )
                    {
                        var namespaceName = usingDirective.Name?.ToString();
                        if (!string.IsNullOrWhiteSpace(namespaceName))
                        {
                            AddNamespaceUsage(namespaceUsages, namespaceName);
                        }
                    }
                }
            }

            analysis.Dependencies = dependencies
                .Values.OrderByDescending(dependency => dependency.UsageCount)
                .ThenBy(dependency => dependency.Name)
                .Take(Math.Max(1, maxDepth) * 20)
                .ToList();
            analysis.NamespaceUsages = namespaceUsages
                .Values.OrderByDescending(namespaceUsage => namespaceUsage.UsageCount)
                .ThenBy(namespaceUsage => namespaceUsage.Namespace)
                .ToList();

            return analysis;
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

                        var symbol = model.GetDeclaredSymbol(member, cancellationToken);
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

        private static IEnumerable<ISymbol> GetSymbols(INamespaceOrTypeSymbol container)
        {
            foreach (var member in container.GetMembers())
            {
                yield return member;

                if (member is INamespaceOrTypeSymbol nestedContainer)
                {
                    foreach (var nestedMember in GetSymbols(nestedContainer))
                    {
                        yield return nestedMember;
                    }
                }
            }
        }

        private static void AddDependency(
            Dictionary<string, ProjectDependency> dependencies,
            string name,
            string type
        )
        {
            var key = type + ":" + name;
            if (!dependencies.TryGetValue(key, out var dependency))
            {
                dependency = new ProjectDependency
                {
                    Name = name,
                    Type = type,
                };
                dependencies[key] = dependency;
            }

            dependency.UsageCount++;
        }

        private static void AddNamespaceUsage(
            Dictionary<string, NamespaceUsage> namespaceUsages,
            string namespaceName
        )
        {
            if (!namespaceUsages.TryGetValue(namespaceName, out var namespaceUsage))
            {
                namespaceUsage = new NamespaceUsage { Namespace = namespaceName };
                namespaceUsages[namespaceName] = namespaceUsage;
            }

            namespaceUsage.UsageCount++;
        }
    }
}
