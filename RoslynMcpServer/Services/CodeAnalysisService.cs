using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using RoslynMcpServer.Models;

namespace RoslynMcpServer.Services
{
    public class CodeAnalysisService : IDisposable
    {
        private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        // Directories that never contain user-authored sources and are expensive to scan.
        private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "obj", ".git", ".vs", ".idea", "node_modules", "TestResults", "artifacts"
        };

        // MSBuild inputs that are imported implicitly and change how projects evaluate.
        private static readonly string[] ImplicitImportFiles =
        {
            "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json"
        };

        // How often a cached workspace is compared against the file system.
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

        private readonly ILogger<CodeAnalysisService> _logger;
        private readonly ConcurrentDictionary<string, CachedWorkspace> _workspaces = new(PathComparer);
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(PathComparer);

        private sealed class CachedWorkspace
        {
            public required MSBuildWorkspace Workspace { get; init; }
            public required Solution Solution { get; set; }
            public required Dictionary<string, DateTime> ProjectFiles { get; init; }
            public required Dictionary<string, DateTime> SourceFiles { get; set; }
            public required string[] SourceRoots { get; init; }
            public required List<string> LoadWarnings { get; init; }
            public DateTime LastChecked { get; set; }
        }

        public CodeAnalysisService(ILogger<CodeAnalysisService> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Resolves a .sln, .slnx, .csproj, or a directory containing exactly one of them, to a full file path.
        /// </summary>
        public static string ResolveSolutionPath(string solutionPath)
        {
            var fullPath = Path.GetFullPath(solutionPath);

            if (Directory.Exists(fullPath))
            {
                var solutions = Directory.GetFiles(fullPath, "*.slnx")
                    .Concat(Directory.GetFiles(fullPath, "*.sln"))
                    .ToArray();
                if (solutions.Length == 1)
                    return solutions[0];
                if (solutions.Length > 1)
                    throw new ArgumentException(
                        $"Multiple solution files found in '{fullPath}': {string.Join(", ", solutions.Select(Path.GetFileName))}. Pass one explicitly.");

                var projects = Directory.GetFiles(fullPath, "*.csproj");
                if (projects.Length == 1)
                    return projects[0];
                if (projects.Length > 1)
                    throw new ArgumentException(
                        $"Multiple project files found in '{fullPath}': {string.Join(", ", projects.Select(Path.GetFileName))}. Pass one explicitly.");

                throw new ArgumentException($"No .sln, .slnx or .csproj file found in '{fullPath}'.");
            }

            if (!File.Exists(fullPath))
                throw new FileNotFoundException("Solution or project file not found.", fullPath);

            return fullPath;
        }

        public async Task<Solution> GetSolutionAsync(string solutionPath, CancellationToken cancellationToken = default)
        {
            return (await GetWorkspaceAsync(solutionPath, cancellationToken)).Solution;
        }

        /// <summary>
        /// Drops the cached workspace for a solution and loads it again from disk.
        /// </summary>
        public async Task<WorkspaceSummary> ReloadAsync(string solutionPath, CancellationToken cancellationToken = default)
        {
            var fullPath = ResolveSolutionPath(solutionPath);
            var gate = _locks.GetOrAdd(fullPath, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken);
            try
            {
                if (_workspaces.TryRemove(fullPath, out var existing))
                    existing.Workspace.Dispose();
            }
            finally
            {
                gate.Release();
            }

            var cached = await GetWorkspaceAsync(fullPath, cancellationToken);
            return new WorkspaceSummary
            {
                SolutionPath = fullPath,
                ProjectCount = cached.Solution.Projects.Count(),
                DocumentCount = cached.Solution.Projects.Sum(p => p.DocumentIds.Count),
                Projects = cached.Solution.Projects.Select(p => p.Name).OrderBy(n => n).ToList(),
                LoadWarnings = cached.LoadWarnings.ToList()
            };
        }

        private async Task<CachedWorkspace> GetWorkspaceAsync(string solutionPath, CancellationToken cancellationToken)
        {
            var fullPath = ResolveSolutionPath(solutionPath);
            var gate = _locks.GetOrAdd(fullPath, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken);
            try
            {
                if (_workspaces.TryGetValue(fullPath, out var cached))
                {
                    if (DateTime.UtcNow - cached.LastChecked < RefreshInterval || TryRefresh(cached))
                        return cached;

                    // Project structure changed (for example a package or project was added through
                    // dotnet-mcp), so the MSBuild evaluation is stale and the workspace must be reloaded.
                    _logger.LogInformation("Project files changed, reloading workspace {SolutionPath}", fullPath);
                    _workspaces.TryRemove(fullPath, out _);
                    cached.Workspace.Dispose();
                }

                var loaded = await LoadAsync(fullPath, cancellationToken);
                _workspaces[fullPath] = loaded;
                return loaded;
            }
            finally
            {
                gate.Release();
            }
        }

        private async Task<CachedWorkspace> LoadAsync(string fullPath, CancellationToken cancellationToken)
        {
            var workspace = MSBuildWorkspace.Create();
            var warnings = new List<string>();
            workspace.RegisterWorkspaceFailedHandler(args =>
            {
                warnings.Add(args.Diagnostic.Message);
                _logger.LogWarning(
                    "MSBuild workspace diagnostic: {Diagnostic}",
                    args.Diagnostic.Message
                );
            });

            Solution solution;
            var extension = Path.GetExtension(fullPath);
            if (extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
            {
                solution = await workspace.OpenSolutionAsync(fullPath, cancellationToken: cancellationToken);
            }
            else if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                var project = await workspace.OpenProjectAsync(fullPath, cancellationToken: cancellationToken);
                solution = project.Solution;
            }
            else
            {
                workspace.Dispose();
                throw new NotSupportedException("Only .sln, .slnx, and .csproj files are supported.");
            }

            var projectDirectories = solution.Projects
                .Select(p => Path.GetDirectoryName(p.FilePath))
                .Where(d => !string.IsNullOrEmpty(d))
                .Select(d => d!)
                .Distinct(PathComparer)
                .ToArray();

            return new CachedWorkspace
            {
                Workspace = workspace,
                Solution = solution,
                ProjectFiles = SnapshotProjectFiles(fullPath, solution, projectDirectories),
                SourceFiles = ScanSourceFiles(projectDirectories),
                SourceRoots = projectDirectories,
                LoadWarnings = warnings,
                LastChecked = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Brings a cached workspace up to date with source edits made outside this server
        /// (an editor, an agent, dotnet format, ...). Returns false when the change needs a full
        /// reload: a solution, project or MSBuild file changed, a restore ran, or source files
        /// were added or removed.
        /// </summary>
        private bool TryRefresh(CachedWorkspace cached)
        {
            foreach (var (file, stamp) in cached.ProjectFiles)
            {
                if (File.GetLastWriteTimeUtc(file) != stamp)
                    return false;
            }

            var current = ScanSourceFiles(cached.SourceRoots);
            if (current.Count != cached.SourceFiles.Count ||
                current.Keys.Any(file => !cached.SourceFiles.ContainsKey(file)))
            {
                return false;
            }

            var solution = cached.Solution;
            foreach (var (file, stamp) in current)
            {
                var previous = cached.SourceFiles[file];
                if (previous == stamp)
                    continue;

                var documentIds = solution.GetDocumentIdsWithFilePath(file);
                if (documentIds.IsEmpty)
                    continue;

                try
                {
                    SourceText text;
                    using (var stream = File.OpenRead(file))
                        text = SourceText.From(stream);

                    foreach (var documentId in documentIds)
                        solution = solution.WithDocumentText(documentId, text, PreservationMode.PreserveIdentity);
                }
                catch (IOException ex)
                {
                    // The file is probably still being written; pick it up on the next request.
                    _logger.LogDebug(ex, "Could not read changed file {File}", file);
                    current[file] = previous;
                }
            }

            cached.Solution = solution;
            cached.SourceFiles = current;
            cached.LastChecked = DateTime.UtcNow;
            return true;
        }

        private static Dictionary<string, DateTime> SnapshotProjectFiles(
            string solutionPath, Solution solution, IEnumerable<string> projectDirectories)
        {
            var files = new HashSet<string>(PathComparer) { solutionPath };

            foreach (var project in solution.Projects)
            {
                if (!string.IsNullOrEmpty(project.FilePath))
                    files.Add(project.FilePath);
            }

            foreach (var directory in projectDirectories)
            {
                // Rewritten by restore, so it changes whenever packages are added, removed or updated.
                files.Add(Path.Combine(directory, "obj", "project.assets.json"));

                // Implicit imports are found by walking up the directory tree. Missing files are
                // recorded as well so that creating one later is detected.
                for (var dir = new DirectoryInfo(directory); dir != null; dir = dir.Parent)
                {
                    foreach (var name in ImplicitImportFiles)
                        files.Add(Path.Combine(dir.FullName, name));
                }
            }

            return files.ToDictionary(file => file, File.GetLastWriteTimeUtc, PathComparer);
        }

        private static Dictionary<string, DateTime> ScanSourceFiles(IEnumerable<string> roots)
        {
            var files = new Dictionary<string, DateTime>(PathComparer);
            var visited = new HashSet<string>(PathComparer);
            var pending = new Stack<string>(roots);

            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                if (!visited.Add(directory))
                    continue;

                try
                {
                    foreach (var file in Directory.EnumerateFiles(directory, "*.cs"))
                        files[file] = File.GetLastWriteTimeUtc(file);

                    foreach (var child in Directory.EnumerateDirectories(directory))
                    {
                        if (!IgnoredDirectories.Contains(Path.GetFileName(child)))
                            pending.Push(child);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Removed or inaccessible directories simply contribute no files.
                }
            }

            return files;
        }

        public async Task<List<DiagnosticResult>> GetDiagnosticsAsync(
            string solutionPath,
            string? filePath,
            DiagnosticSeverity minimumSeverity,
            CancellationToken cancellationToken = default
        )
        {
            var solution = await GetSolutionAsync(solutionPath, cancellationToken);
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
            var solution = await GetSolutionAsync(solutionPath, cancellationToken);
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
            var solution = await GetSolutionAsync(solutionPath, cancellationToken);
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
                    Version = string.Empty,
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

        public void Dispose()
        {
            foreach (var cached in _workspaces.Values)
                cached.Workspace.Dispose();
            _workspaces.Clear();
        }
    }
}
