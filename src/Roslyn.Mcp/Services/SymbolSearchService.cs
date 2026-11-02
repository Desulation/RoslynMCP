using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.Extensions.Logging;
using Roslyn.Mcp.Models;
using System.Text.RegularExpressions;
using MsSymbolInfo = Microsoft.CodeAnalysis.SymbolInfo;
using SymbolInfo = Roslyn.Mcp.Models.SymbolInfo;

namespace Roslyn.Mcp.Services
{
    public class SymbolSearchService
    {
        private readonly ILogger<SymbolSearchService> _logger;
        private readonly WorkspaceManager _workspaceManager;
        private readonly SymbolResolver _resolver;

        public SymbolSearchService(
            ILogger<SymbolSearchService> logger, WorkspaceManager workspaceManager, SymbolResolver resolver)
        {
            _logger = logger;
            _workspaceManager = workspaceManager;
            _resolver = resolver;
        }

        // "OrderService<T>" for a type and "OrderService<T>.Save" for a member, so a pattern such as
        // "OrderService<*>" matches without spelling out the namespace.
        private static readonly SymbolDisplayFormat TypeQualifiedFormat = new(
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            memberOptions: SymbolDisplayMemberOptions.IncludeContainingType);

        /// <summary>
        /// Finds source-declared symbols whose name, type-qualified name or full display string matches the
        /// wildcard <paramref name="pattern"/> (<c>*</c> and <c>?</c>), most relevant first, one result per
        /// declaration even when a project has several target frameworks.
        /// </summary>
        public async Task<IReadOnlyList<SymbolSearchResult>> SearchSymbolsAsync(
            string pattern, string solutionPath, SymbolTypeFilter filter, bool ignoreCase, CancellationToken cancellationToken)
        {
            var solution = await _workspaceManager.GetSolutionAsync(solutionPath, cancellationToken);
            var regex = CreateWildcardRegex(pattern, ignoreCase);

            var perProject = await Task.WhenAll(solution.Projects
                .Where(p => p.SupportsCompilation)
                .Select(project => SearchProjectAsync(project, regex, filter, cancellationToken)));

            return perProject
                .SelectMany(matches => matches)
                .DistinctBy(match => SymbolResolver.DeclarationKey(match.Symbol))
                .Select(match => CreateSearchResult(match.Symbol, match.Project))
                .OrderByDescending(r => CalculateRelevanceScore(r, pattern))
                .ThenBy(r => r.Name)
                .ToList();
        }

        private async Task<List<(ISymbol Symbol, Project Project)>> SearchProjectAsync(
            Project project, Regex regex, SymbolTypeFilter filter, CancellationToken cancellationToken)
        {
            var matches = new List<(ISymbol Symbol, Project Project)>();

            try
            {
                var compilation = await project.GetCompilationAsync(cancellationToken);
                if (compilation == null) return matches;

                foreach (var symbol in SymbolResolver.EnumerateDeclaredSymbols(compilation))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (filter.Matches(symbol) &&
                        (regex.IsMatch(symbol.Name) ||
                         regex.IsMatch(symbol.ToDisplayString(TypeQualifiedFormat)) ||
                         regex.IsMatch(symbol.ToDisplayString())))
                    {
                        matches.Add((symbol, project));
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One project that fails to compile should not hide the matches in the others.
                _logger.LogError(ex, "Error searching project: {ProjectName}", project.Name);
            }

            return matches;
        }

        private static Regex CreateWildcardRegex(string pattern, bool ignoreCase)
        {
            // Convert wildcard pattern to regex
            var regexPattern = Regex.Escape(pattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".");

            var options = RegexOptions.Compiled;
            if (ignoreCase) options |= RegexOptions.IgnoreCase;

            return new Regex($"^{regexPattern}$", options);
        }

        private static SymbolSearchResult CreateSearchResult(ISymbol symbol, Project project)
        {
            var location = symbol.Locations.FirstOrDefault();
            var lineNumber = location?.GetLineSpan().StartLinePosition.Line + 1 ?? 0;

            return new SymbolSearchResult
            {
                Name = symbol.Name,
                FullName = symbol.ToDisplayString(),
                Category = GetSymbolCategory(symbol),
                Location = $"{location?.SourceTree?.FilePath}:{lineNumber}",
                ProjectName = project.Name,
                FilePath = location?.SourceTree?.FilePath ?? "",
                LineNumber = lineNumber,
                Summary = GetSymbolSummary(symbol),
                Accessibility = symbol.DeclaredAccessibility.ToString().ToLower(),
                SymbolKind = symbol.Kind,
                Namespace = symbol.ContainingNamespace?.ToDisplayString() ?? ""
            };
        }

        private static string GetSymbolCategory(ISymbol symbol)
        {
            return symbol switch
            {
                INamedTypeSymbol namedType => namedType.TypeKind.ToString(),
                IMethodSymbol => "Method",
                IPropertySymbol => "Property",
                IFieldSymbol => "Field",
                IEventSymbol => "Event",
                INamespaceSymbol => "Namespace",
                _ => symbol.Kind.ToString()
            };
        }

        private static string GetSymbolSummary(ISymbol symbol)
        {
            return symbol switch
            {
                IMethodSymbol method => "(" + string.Join(", ",
                    method.Parameters.Select(p => $"{p.Type.Name} {p.Name}")) + ")",
                IPropertySymbol property => $": {property.Type.Name}",
                IFieldSymbol field => $": {field.Type.Name}",
                INamedTypeSymbol type => $"{type.TypeKind} with {type.GetMembers().Length} members",
                _ => ""
            };
        }

        private static double CalculateRelevanceScore(SymbolSearchResult result, string searchPattern)
        {
            double score = 0;

            // Exact match gets highest score
            if (result.Name.Equals(searchPattern.Replace("*", "").Replace("?", ""),
                StringComparison.OrdinalIgnoreCase))
                score += 100;

            // Prefix match
            if (result.Name.StartsWith(searchPattern.Replace("*", ""),
                StringComparison.OrdinalIgnoreCase))
                score += 50;

            // Length penalty (shorter names are more relevant)
            score -= result.Name.Length * 0.1;

            // Public accessibility bonus
            if (result.Accessibility == "public")
                score += 10;

            return score;
        }

        public async Task<IEnumerable<ReferenceResult>> FindReferencesAsync(
            string symbolName, string solutionPath, bool includeDefinition)
        {
            var solution = await _workspaceManager.GetSolutionAsync(solutionPath);
            var targetSymbols = await _resolver.FindByNameAsync(solution, symbolName);

            var allReferences = new List<ReferenceResult>();

            foreach (var symbol in targetSymbols)
            {
                var references = await SymbolFinder.FindReferencesAsync(symbol, solution);

                foreach (var referencedSymbol in references)
                {
                    foreach (var location in referencedSymbol.Locations)
                    {
                        // Check if this location is a definition by comparing with the symbol's definition locations
                        var isDefinition = referencedSymbol.Definition.Locations.Any(defLoc =>
                            defLoc.SourceTree == location.Location.SourceTree &&
                            defLoc.SourceSpan == location.Location.SourceSpan);

                        if (!includeDefinition && isDefinition)
                            continue;

                        var reference = await CreateReferenceResultAsync(location, symbol, isDefinition);
                        if (reference != null)
                            allReferences.Add(reference);
                    }
                }
            }

            return allReferences
                .GroupBy(r => $"{r.DocumentPath}:{r.LineNumber}")
                .Select(g => g.First()) // Remove duplicates
                .OrderBy(r => r.DocumentPath)
                .ThenBy(r => r.LineNumber);
        }

        public async Task<List<SymbolLocationResult>> FindImplementationsAsync(
            string symbolName, string solutionPath, CancellationToken cancellationToken = default)
        {
            var solution = await _workspaceManager.GetSolutionAsync(solutionPath, cancellationToken);
            var targets = await _resolver.FindByNameAsync(solution, symbolName, cancellationToken);
            var results = new List<SymbolLocationResult>();

            foreach (var target in targets)
            {
                var found = new List<(ISymbol Symbol, string Relation)>();

                if (target is INamedTypeSymbol type)
                {
                    if (type.TypeKind == TypeKind.Interface)
                    {
                        found.AddRange((await SymbolFinder.FindImplementationsAsync(type, solution, transitive: true, cancellationToken: cancellationToken))
                            .Select(s => ((ISymbol)s, $"implements {type.Name}")));
                        found.AddRange((await SymbolFinder.FindDerivedInterfacesAsync(type, solution, transitive: true, cancellationToken: cancellationToken))
                            .Select(s => ((ISymbol)s, $"extends {type.Name}")));
                    }
                    else if (type.TypeKind == TypeKind.Class)
                    {
                        found.AddRange((await SymbolFinder.FindDerivedClassesAsync(type, solution, transitive: true, cancellationToken: cancellationToken))
                            .Select(s => ((ISymbol)s, $"derives from {type.Name}")));
                    }
                }
                else if (target is IMethodSymbol or IPropertySymbol or IEventSymbol)
                {
                    var owner = target.ContainingType?.Name ?? "";
                    found.AddRange((await SymbolFinder.FindImplementationsAsync(target, solution, cancellationToken: cancellationToken))
                        .Select(s => (s, $"implements {owner}.{target.Name}")));
                    found.AddRange((await SymbolFinder.FindOverridesAsync(target, solution, cancellationToken: cancellationToken))
                        .Select(s => (s, $"overrides {owner}.{target.Name}")));
                }

                foreach (var (symbol, relation) in found)
                {
                    foreach (var location in symbol.Locations.Where(l => l.IsInSource))
                        results.Add(await CreateLocationResultAsync(solution, symbol, location, relation, cancellationToken));
                }
            }

            return results
                .DistinctBy(r => (r.DisplayName, r.FilePath, r.LineNumber))
                .OrderBy(r => r.FilePath)
                .ThenBy(r => r.LineNumber)
                .ToList();
        }

        public async Task<List<SymbolLocationResult>> FindCallersAsync(
            string symbolName, string solutionPath, CancellationToken cancellationToken = default)
        {
            var solution = await _workspaceManager.GetSolutionAsync(solutionPath, cancellationToken);
            var targets = (await _resolver.FindByNameAsync(solution, symbolName, cancellationToken))
                .Where(s => s is IMethodSymbol or IPropertySymbol or IEventSymbol);
            var results = new List<SymbolLocationResult>();

            foreach (var target in targets)
            {
                var callers = await SymbolFinder.FindCallersAsync(target, solution, cancellationToken);
                foreach (var caller in callers)
                {
                    var relation = caller.IsDirect ? $"calls {target.Name}" : $"calls {target.Name} (via interface or override)";
                    foreach (var location in caller.Locations.Where(l => l.IsInSource))
                        results.Add(await CreateLocationResultAsync(solution, caller.CallingSymbol, location, relation, cancellationToken));
                }
            }

            return results
                .DistinctBy(r => (r.DisplayName, r.FilePath, r.LineNumber))
                .OrderBy(r => r.FilePath)
                .ThenBy(r => r.LineNumber)
                .ToList();
        }

        private static async Task<SymbolLocationResult> CreateLocationResultAsync(
            Solution solution, ISymbol symbol, Location location, string relation, CancellationToken cancellationToken)
        {
            var lineSpan = location.GetLineSpan();
            var lineText = "";
            var document = solution.GetDocument(location.SourceTree);
            if (document != null)
            {
                var text = await document.GetTextAsync(cancellationToken);
                lineText = text.Lines[lineSpan.StartLinePosition.Line].ToString().Trim();
            }

            return new SymbolLocationResult
            {
                DisplayName = symbol.ToDisplayString(),
                Kind = symbol is INamedTypeSymbol type ? type.TypeKind.ToString() : symbol.Kind.ToString(),
                Relation = relation,
                ProjectName = document?.Project.Name ?? "",
                FilePath = lineSpan.Path,
                LineNumber = lineSpan.StartLinePosition.Line + 1,
                LineText = lineText,
            };
        }

        private static async Task<ReferenceResult?> CreateReferenceResultAsync(
            ReferenceLocation location, ISymbol symbol, bool isDefinition)
        {
            if (location.Document == null) return null;

            var document = location.Document;
            var sourceText = await document.GetTextAsync();
            var lineSpan = location.Location.GetLineSpan();

            // Get surrounding context
            var lineNumber = lineSpan.StartLinePosition.Line;
            var line = sourceText.Lines[lineNumber];
            var contextStart = Math.Max(0, lineNumber - 2);
            var contextEnd = Math.Min(sourceText.Lines.Count - 1, lineNumber + 2);

            var context = sourceText.Lines
                .Skip(contextStart)
                .Take(contextEnd - contextStart + 1)
                .Select((l, i) => $"{contextStart + i + 1,4}: {l}")
                .ToList();

            return new ReferenceResult
            {
                SymbolName = symbol.Name,
                DocumentPath = document.FilePath ?? "",
                ProjectName = document.Project.Name,
                LineNumber = lineNumber + 1,
                ColumnNumber = lineSpan.StartLinePosition.Character + 1,
                LineText = line.ToString(),
                Context = context,
                IsDefinition = isDefinition,
                ReferenceKind = DetermineReferenceKind(location.Location, symbol)
            };
        }

        private static string DetermineReferenceKind(Location location, ISymbol symbol)
        {
            // This is a simplified implementation
            // A more sophisticated version would analyze the syntax context
            return symbol.Kind switch
            {
                SymbolKind.Method => "Method Call",
                SymbolKind.Property => "Property Access",
                SymbolKind.Field => "Field Access",
                SymbolKind.NamedType => "Type Reference",
                _ => "Reference"
            };
        }

        public async Task<SymbolInfo?> GetSymbolInfoAsync(string symbolName, string solutionPath)
        {
            var solution = await _workspaceManager.GetSolutionAsync(solutionPath);
            var symbols = await _resolver.FindByNameAsync(solution, symbolName);
            var symbol = symbols.FirstOrDefault();
            var otherMatches = symbols.Skip(1).Select(s => s.ToDisplayString()).Take(10).ToList();

            if (symbol == null) return null;

            var info = new SymbolInfo
            {
                Name = symbol.Name,
                FullName = symbol.ToDisplayString(),
                Kind = symbol.Kind.ToString(),
                Accessibility = symbol.DeclaredAccessibility.ToString(),
                DeclaringType = symbol.ContainingType?.Name ?? "",
                Namespace = symbol.ContainingNamespace?.ToDisplayString() ?? "",
                Assembly = symbol.ContainingAssembly?.Name ?? "",
                Documentation = symbol.GetDocumentationCommentXml() ?? ""
            };

            // Add method-specific information
            if (symbol is IMethodSymbol method)
            {
                info.Parameters = method.Parameters.Select(p => $"{p.Type.ToDisplayString()} {p.Name}").ToList();
                info.ReturnType = method.ReturnType.ToDisplayString();
            }

            // Add property-specific information
            if (symbol is IPropertySymbol property)
            {
                info.ReturnType = property.Type.ToDisplayString();
            }

            // Add attributes
            info.Attributes = symbol.GetAttributes()
                .Select(attr => attr.AttributeClass?.Name ?? "")
                .Where(name => !string.IsNullOrEmpty(name))
                .ToList();

            // Add source location
            var location = symbol.Locations.FirstOrDefault();
            if (location != null && location.SourceTree != null)
            {
                var lineSpan = location.GetLineSpan();
                info.SourceLocation = $"{location.SourceTree.FilePath}:{lineSpan.StartLinePosition.Line + 1}";
            }

            info.OtherMatches = otherMatches;

            return info;
        }
    }
}
