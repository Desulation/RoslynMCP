using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using Roslyn.Mcp.Models;

namespace Roslyn.Mcp.Services
{
    /// <summary>
    /// Owns one cached <see cref="MSBuildWorkspace"/> per resolved solution path and keeps it in
    /// step with the file system.
    /// </summary>
    public sealed class WorkspaceManager : IDisposable
    {
        // Matches the default file system case sensitivity (NTFS and APFS ignore case).
        private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
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

        private readonly ILogger<WorkspaceManager> _logger;

        // How often a cached workspace is compared against the file system.
        private readonly TimeSpan _refreshInterval;

        private readonly ConcurrentDictionary<string, CachedWorkspace> _workspaces = new(PathComparer);
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(PathComparer);
        private int _loadCount;

        private sealed class CachedWorkspace
        {
            public required MSBuildWorkspace Workspace { get; init; }
            public required Solution Solution { get; set; }
            public required Dictionary<string, DateTime> ProjectFiles { get; init; }
            public required Dictionary<string, DateTime> SourceFiles { get; set; }
            public required string[] SourceRoots { get; init; }
            public required List<string> LoadWarnings { get; init; }
            public DateTime LastChecked { get; set; }

            // 1 until the load warnings have been announced once (see TryTakeLoadHint). Read and
            // cleared with Interlocked so taking the hint never waits for the per-solution gate.
            public int HintPending;
        }

        public WorkspaceManager(ILogger<WorkspaceManager> logger, WorkspaceOptions options)
        {
            _logger = logger;
            _refreshInterval = options.RefreshInterval;
        }

        /// <summary>
        /// Number of full MSBuild loads performed so far.
        /// </summary>
        public int LoadCount => Volatile.Read(ref _loadCount);

        /// <summary>
        /// Resolves a .sln, .slnx, .csproj, or a directory containing exactly one of them, to a full file path.
        /// </summary>
        public static string ResolveSolutionPath(string solutionPath)
        {
            // Trimming a trailing separator keeps the cache key the same for "dir" and "dir/".
            var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(solutionPath));

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

        /// <summary>
        /// Returns true exactly once per load when that load produced MSBuild warnings, then false
        /// until the workspace is loaded again. Also false when the solution is not loaded, including
        /// while a load is still in progress. Lock-free: it never waits for a load or refresh.
        /// </summary>
        public bool TryTakeLoadHint(string solutionPath, out int warningCount)
        {
            var fullPath = ResolveSolutionPath(solutionPath);
            if (_workspaces.TryGetValue(fullPath, out var cached) &&
                Interlocked.Exchange(ref cached.HintPending, 0) == 1)
            {
                warningCount = cached.LoadWarnings.Count;
                return true;
            }

            warningCount = 0;
            return false;
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
                    if (DateTime.UtcNow - cached.LastChecked < _refreshInterval || TryRefresh(cached))
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
            Interlocked.Increment(ref _loadCount);

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
            try
            {
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
                    throw new NotSupportedException("Only .sln, .slnx, and .csproj files are supported.");
                }
            }
            catch
            {
                // A cancelled or failed load would otherwise leave the workspace and its build host running.
                workspace.Dispose();
                throw;
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
                LastChecked = DateTime.UtcNow,
                // Published to readers by the ConcurrentDictionary insert in GetWorkspaceAsync.
                HintPending = warnings.Count > 0 ? 1 : 0
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
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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

        public void Dispose()
        {
            foreach (var cached in _workspaces.Values)
                cached.Workspace.Dispose();
            _workspaces.Clear();
        }
    }
}
