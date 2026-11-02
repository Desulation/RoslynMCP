using Microsoft.CodeAnalysis;

namespace Roslyn.Mcp.Services
{
    /// <summary>
    /// The symbol kinds a search should return, parsed from a comma-separated list of words such as
    /// "class, interface, method". Type words filter on <see cref="INamedTypeSymbol.TypeKind"/>:
    /// "class" includes record classes, "struct" includes record structs, and "record" is any record.
    /// </summary>
    public sealed class SymbolTypeFilter
    {
        /// <summary>The words <see cref="Parse"/> accepts, in the order error messages list them.</summary>
        public static readonly IReadOnlyList<string> AcceptedWords =
        [
            "class", "interface", "struct", "enum", "record", "delegate",
            "method", "property", "field", "event", "namespace",
        ];

        private readonly HashSet<TypeKind> _typeKinds = [];
        private readonly HashSet<SymbolKind> _symbolKinds = [];
        private bool _records;

        private SymbolTypeFilter()
        {
        }

        /// <summary>
        /// Parses a comma-separated list of <see cref="AcceptedWords"/>, ignoring case and surrounding spaces.
        /// </summary>
        /// <exception cref="ArgumentException">A word is not one of <see cref="AcceptedWords"/>, or no word is given.</exception>
        public static SymbolTypeFilter Parse(string symbolTypes)
        {
            ArgumentNullException.ThrowIfNull(symbolTypes);

            var filter = new SymbolTypeFilter();
            foreach (var word in symbolTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                switch (word.ToLowerInvariant())
                {
                    case "class": filter._typeKinds.Add(TypeKind.Class); break;
                    case "interface": filter._typeKinds.Add(TypeKind.Interface); break;
                    case "struct": filter._typeKinds.Add(TypeKind.Struct); break;
                    case "enum": filter._typeKinds.Add(TypeKind.Enum); break;
                    case "delegate": filter._typeKinds.Add(TypeKind.Delegate); break;
                    case "record": filter._records = true; break;
                    case "method": filter._symbolKinds.Add(SymbolKind.Method); break;
                    case "property": filter._symbolKinds.Add(SymbolKind.Property); break;
                    case "field": filter._symbolKinds.Add(SymbolKind.Field); break;
                    case "event": filter._symbolKinds.Add(SymbolKind.Event); break;
                    case "namespace": filter._symbolKinds.Add(SymbolKind.Namespace); break;
                    default:
                        throw new ArgumentException(
                            $"unknown symbol type '{word}'. Accepted: {string.Join(", ", AcceptedWords)}");
                }
            }

            if (filter._typeKinds.Count == 0 && filter._symbolKinds.Count == 0 && !filter._records)
                throw new ArgumentException($"no symbol types given. Accepted: {string.Join(", ", AcceptedWords)}");

            return filter;
        }

        public bool Matches(ISymbol symbol)
        {
            ArgumentNullException.ThrowIfNull(symbol);

            return symbol is INamedTypeSymbol type
                ? _typeKinds.Contains(type.TypeKind) || (_records && type.IsRecord)
                : _symbolKinds.Contains(symbol.Kind);
        }
    }
}
