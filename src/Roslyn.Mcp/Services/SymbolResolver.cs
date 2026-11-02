using Microsoft.CodeAnalysis;

namespace Roslyn.Mcp.Services
{
    /// <summary>
    /// Resolves user-supplied symbol names ("Save", "OrderService.Save", "Sample.Core.Order") to the
    /// symbols declared in a solution's source.
    /// </summary>
    public sealed class SymbolResolver
    {
        private readonly WorkspaceManager _workspaces;

        public SymbolResolver(WorkspaceManager workspaces)
        {
            _workspaces = workspaces;
        }

        /// <summary>
        /// Finds every source-declared symbol matching <paramref name="symbolName"/> (see
        /// <see cref="MatchesName"/>), one result per symbol even when a project has several target frameworks.
        /// </summary>
        public async Task<List<ISymbol>> FindByNameAsync(
            string solutionPath, string symbolName, CancellationToken cancellationToken = default)
        {
            var solution = await _workspaces.GetSolutionAsync(solutionPath, cancellationToken);
            return await FindByNameAsync(solution, symbolName, cancellationToken);
        }

        /// <summary>
        /// Same as the path overload, for callers that already hold the solution snapshot they will
        /// pass to <c>SymbolFinder</c>.
        /// </summary>
        public async Task<List<ISymbol>> FindByNameAsync(
            Solution solution, string symbolName, CancellationToken cancellationToken = default)
        {
            var symbols = new List<ISymbol>();

            foreach (var project in solution.Projects.Where(p => p.SupportsCompilation))
            {
                var compilation = await project.GetCompilationAsync(cancellationToken);
                if (compilation != null)
                    symbols.AddRange(EnumerateDeclaredSymbols(compilation).Where(s => MatchesName(s, symbolName)));
            }

            // Multi-targeted projects produce one copy of each symbol per target framework.
            return symbols
                .DistinctBy(s => (s.ToDisplayString(), FormatLocation(s.Locations.FirstOrDefault())))
                .ToList();
        }

        /// <summary>
        /// Matches a simple name ("Save") or a name qualified by its containers ("OrderService.Save",
        /// "Sample.Core.OrderService"), ignoring case. Surrounding whitespace and one trailing "()" are
        /// ignored. Generic containers match by their bare name, so "OrderService.Save" finds
        /// <c>OrderService&lt;T&gt;.Save</c>.
        /// </summary>
        public static bool MatchesName(ISymbol symbol, string symbolName)
        {
            var query = symbolName.Trim();
            if (query.EndsWith("()", StringComparison.Ordinal))
                query = query[..^2];

            if (query.Length == 0)
                return false;

            if (symbol.Name.Equals(query, StringComparison.OrdinalIgnoreCase))
                return true;

            if (!query.Contains('.') || symbol.ContainingSymbol == null)
                return false;

            var qualifiedName = $"{symbol.ContainingSymbol.ToDisplayString()}.{symbol.Name}";
            return EqualsOrEndsWith(qualifiedName, query) || EqualsOrEndsWith(MetadataQualifiedName(symbol), query);
        }

        /// <summary>
        /// Every symbol declared in the compilation's own source: namespaces, types and their members,
        /// without compiler-generated members or property/event accessors.
        /// </summary>
        public static IEnumerable<ISymbol> EnumerateDeclaredSymbols(Compilation compilation)
        {
            // compilation.GlobalNamespace would also walk every referenced assembly and repeat
            // project-referenced symbols once per referencing project.
            return GetAllSymbolsRecursive(compilation.Assembly.GlobalNamespace);
        }

        private static bool EqualsOrEndsWith(string candidate, string query) =>
            candidate.Equals(query, StringComparison.OrdinalIgnoreCase) ||
            candidate.EndsWith("." + query, StringComparison.OrdinalIgnoreCase);

        // Containing namespace and type names without type parameters, e.g. "Sample.Core.OrderService.Save".
        private static string MetadataQualifiedName(ISymbol symbol)
        {
            var parts = new List<string> { symbol.Name };
            for (var container = symbol.ContainingSymbol;
                 container is INamespaceOrTypeSymbol and not INamespaceSymbol { IsGlobalNamespace: true };
                 container = container.ContainingSymbol)
            {
                parts.Add(container.Name);
            }

            parts.Reverse();
            return string.Join(".", parts);
        }

        private static IEnumerable<ISymbol> GetAllSymbolsRecursive(INamespaceSymbol namespaceSymbol)
        {
            foreach (var member in namespaceSymbol.GetMembers())
            {
                yield return member;

                switch (member)
                {
                    case INamespaceSymbol nestedNamespace:
                        foreach (var nested in GetAllSymbolsRecursive(nestedNamespace))
                            yield return nested;
                        break;

                    case INamedTypeSymbol namedType:
                        foreach (var typeMember in GetTypeMembersRecursive(namedType))
                            yield return typeMember;
                        break;
                }
            }
        }

        private static IEnumerable<ISymbol> GetTypeMembersRecursive(INamedTypeSymbol type)
        {
            foreach (var member in type.GetMembers())
            {
                // Skip compiler-generated members and property/event accessors.
                if (member.IsImplicitlyDeclared || member is IMethodSymbol { AssociatedSymbol: not null })
                    continue;

                yield return member;

                if (member is INamedTypeSymbol nestedType)
                {
                    foreach (var nested in GetTypeMembersRecursive(nestedType))
                        yield return nested;
                }
            }
        }

        private static string FormatLocation(Location? location)
        {
            if (location == null || !location.IsInSource)
                return "";

            var lineSpan = location.GetLineSpan();
            return $"{lineSpan.Path}:{lineSpan.StartLinePosition.Line + 1}";
        }
    }
}
