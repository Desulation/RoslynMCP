# Roslyn MCP — hardening, restructure and packaging

Date: 2026-11-02
Status: approved in conversation, awaiting written review

## 1. Purpose

`RoslynMcpServer` is a read-only MCP server that gives a model the C# compiler's
semantic view of a codebase. It exists to complement
[jongalloway/dotnet-mcp](https://github.com/jongalloway/dotnet-mcp), which drives
the .NET SDK (create, add packages, build, test, run) and has no Roslyn side at all:
its `Analyze`, `Dependencies` and `Validate` actions evaluate MSBuild; its
"diagnostics" are explanations of CS/MSB/NU error codes; `Format` shells out to
`dotnet format`.

This round makes the server release-quality as that companion:

- fix the behaviour bugs in the ten existing tools without changing their names or
  parameters (one parameter is renamed where it was meaningless, see §5.3);
- delete the dead code and the HTTP transport;
- restructure the repository to match the owner's `Desulation/ccusage` fork's .NET
  conventions (`src/` + `tests/`, central package versions, warnings as errors, MSTest
  on Microsoft.Testing.Platform, `AGENTS.md`, CI on ubuntu + windows);
- add a test suite that runs against a checked-in sample solution;
- ship a framework-dependent `.mcpb` bundle;
- document, in the README and `AGENTS.md`, how this server expands on dotnet-mcp.

Out of scope: new tools, structured/JSON output, pagination, VB.NET, Native AOT
(`MSBuildWorkspace` loads MSBuild by reflection and cannot be trimmed or AOT-compiled).

## 2. Repository layout

The fork nests .NET under `dotnet/` because it shares the root with Rust and a docs
site. This repository is .NET only, so the same files sit at the root.

```
RoslynMCP/
  Roslyn.Mcp.slnx                  solution folders /src/ and /tests/
  global.json                      sdk 10.0.x, rollForward latestFeature, test.runner Microsoft.Testing.Platform
  Directory.Build.props            see §2.1
  Directory.Packages.props         every PackageVersion; PackageReferences carry no Version
  nuget.config                     <clear/> + nuget.org only
  AGENTS.md                        layout, commands, conventions, dotnet-mcp split
  README.md                        single README (the two existing ones are merged)
  LICENSE                          MIT
  .gitignore                       standard Visual Studio / .NET ignore
  .mcp.json                        registers the built roslyn-mcp for Claude Code in this repo
  .github/workflows/ci.yaml        build + test matrix
  .github/workflows/build-artifacts.yaml   packs and uploads the .mcpb
  docs/superpowers/specs/          this document and later ones
  scripts/setup.ps1                client-config helper (rewritten, see §7.3)
  packaging/manifest.json          .mcpb manifest template
  packaging/pack.ps1               publish + zip into roslyn-mcp.mcpb
  src/Roslyn.Mcp/
    Roslyn.Mcp.csproj              Microsoft.NET.Sdk, OutputType Exe, AssemblyName roslyn-mcp, RootNamespace Roslyn.Mcp
    Program.cs
    Models/SearchModels.cs
    Services/WorkspaceManager.cs
    Services/WorkspaceOptions.cs
    Services/CodeAnalysisService.cs
    Services/SymbolResolver.cs
    Services/SymbolSearchService.cs
    Services/SecurityValidator.cs
    Formatting/ResultFormatter.cs
    Tools/CodeNavigationTools.cs
  tests/Roslyn.Mcp.Tests/
    Roslyn.Mcp.Tests.csproj        MSTest, EnableMSTestRunner, NoWarn CA1707;CA1861
    MSTestSettings.cs              [assembly: Parallelize(Scope = ExecutionScope.ClassLevel)]
    TestPaths.cs                   FixturesDir / ServerExePath from AssemblyMetadata
    AssemblySetup.cs               [AssemblyInitialize] → MSBuildLocator.RegisterDefaults()
    Services/  Formatting/  Tools/  E2E/
  tests/fixtures/SampleSolution/   see §6.1
```

Deleted: `RoslynMcpServer/` (moved to `src/Roslyn.Mcp/`), `test-installation.bat`,
`test-installation.sh`, `Dockerfile`, `RoslynMcpServer.csproj.user`,
`RoslynMcpServer.http`, `Properties/launchSettings.json`, the server-level `README.md`
and `.gitignore`, `claude_desktop_config.json` at the root (its content, with valid
escaping, moves into the README), `RoslynMCP.slnx`.

### 2.1 Build properties

`Directory.Build.props`, applied to both projects:

| Property | Value |
|---|---|
| TargetFramework | net10.0 |
| Nullable, ImplicitUsings | enable |
| LangVersion | latest |
| TreatWarningsAsErrors | true |
| EnforceCodeStyleInBuild | true |
| AnalysisLevel | latest-recommended |
| InvariantGlobalization | true |
| ManagePackageVersionsCentrally | true |
| Version | 1.0.0 (single source for the assembly and the .mcpb manifest) |

Not carried over from the fork: `PublishAot`, `IsAotCompatible`, `IlcTreatWarningsAsErrors`,
`StripSymbols`, `UseSystemResourceKeys` (all AOT/trim related).

Packages (`Directory.Packages.props`): `ModelContextProtocol`, `Microsoft.Extensions.Hosting`,
`Microsoft.CodeAnalysis.CSharp.Workspaces`, `Microsoft.CodeAnalysis.Workspaces.MSBuild`,
`Microsoft.Build.Locator`, `MSTest`, `Microsoft.Build.Framework`. The server project
keeps `Microsoft.Build.Framework` with `ExcludeAssets="runtime" PrivateAssets="all"`
(it pins the compile-time version without copying the assembly), and `PrivateAssets`
keeps it out of the test project: neither project may have `Microsoft.Build.*`
assemblies in its output, or `MSBuildLocator` throws at startup. Removed:
`Microsoft.Extensions.Caching.StackExchangeRedis`, `ModelContextProtocol.AspNetCore`,
`Microsoft.CodeAnalysis.CSharp` (transitive via Workspaces).

### 2.2 Naming

Project `Roslyn.Mcp`, executable `roslyn-mcp`, namespaces `Roslyn.Mcp.{Models,Services,Formatting,Tools}`.
The MCP server name stays `roslyn`, the tool prefix stays `roslyn_`, so client
configurations and allowlists are unaffected except for the executable path.

## 3. Transport and startup

Stdio only. `Program.cs`:

1. `MSBuildLocator.RegisterDefaults()` before any Roslyn MSBuild type is touched; on
   failure, log to stderr and exit 1.
2. `--version` prints the assembly version to stdout and exits 0 (used by `setup.ps1`
   and CI smoke checks).
3. `--stdio` is accepted and ignored, so existing client configs keep working.
4. `Host.CreateApplicationBuilder(args)`; logging cleared and routed to stderr at all
   levels (stdout carries the protocol); services registered (§4); `AddMcpServer`
   with `ServerInfo { Name = "roslyn", Title = "Roslyn C# Code Analysis", Version }`
   and the server instructions; `WithStdioServerTransport()`; `WithToolsFromAssembly()`.

Server instructions keep their current content (the dotnet-mcp split, the suggested
workflow, `solutionPath` semantics, the CS0246/restore hint) with the dependency-tool
wording updated for §5.3.

## 4. Code structure

Dependency direction: `Tools → {SymbolSearchService, CodeAnalysisService} → SymbolResolver → WorkspaceManager`.
Formatters depend only on `Models`. No service references a tool or a formatter.

### 4.1 Deleted

- `Services/IncrementalAnalyzer.cs` (`IncrementalAnalyzer`, `FileAnalysisCache`, `EnumerableExtensions.Batch`)
- `Services/CacheManager.cs` (`IPersistentCache`, `FilePersistentCache`, `MultiLevelCacheManager`)
- `Services/DiagnosticLogger.cs`
- `Models.AnalysisResult`, `NamespaceUsage.UsedTypes`, `ProjectDependency.Version`
- The `IMemoryCache` field in `SymbolSearchService` and `services.AddMemoryCache()`
- `UseHttpsRedirection()` and all of the HTTP branch

### 4.2 `WorkspaceManager` (new; extracted from `CodeAnalysisService`)

Owns one cached `MSBuildWorkspace` per resolved solution path. Public surface:

- `static string ResolveSolutionPath(string)` — `.sln`/`.slnx`/`.csproj` file or a
  directory containing exactly one; `ArgumentException` with a message naming the
  candidates when ambiguous or empty; `FileNotFoundException` when missing.
- `Task<Solution> GetSolutionAsync(string solutionPath, CancellationToken)`
- `Task<WorkspaceSummary> ReloadAsync(string solutionPath, CancellationToken)`
- `bool TryTakeLoadHint(string solutionPath, out int warningCount)` — for §5.8: returns
  true exactly once per load when that load produced MSBuild warnings, then false until
  the workspace is loaded again.
- `Dispose()`

Behaviour is the current `GetWorkspaceAsync`/`LoadAsync`/`TryRefresh`/`SnapshotProjectFiles`/`ScanSourceFiles`
unchanged, with `RefreshInterval` read from `WorkspaceOptions` (default 2 s) instead of
a constant. Per-path `SemaphoreSlim` gating stays.

### 4.3 `CodeAnalysisService`

Answers questions about a solution obtained from `WorkspaceManager`:

- `GetDiagnosticsAsync(solutionPath, filePath?, minimumSeverity, ct)` — unchanged logic.
- `GetDocumentOutlineAsync(solutionPath, filePath, ct)` — unchanged logic.
- `AnalyzeDependenciesAsync(solutionPath, maxNamespaces, ct)` — see §5.3.
- `AnalyzeComplexityAsync(solutionPath, threshold, ct)` — moved from the tool class; see §5.7.

### 4.4 `SymbolResolver` (new; extracted from `SymbolSearchService`)

- `Task<List<ISymbol>> FindByNameAsync(Solution, string symbolName, CancellationToken)`
- `static bool MatchesName(ISymbol, string symbolName)` — see §5.6.

Walks `compilation.Assembly.GlobalNamespace` of every project that supports
compilation, skips implicitly declared members and accessors, dedupes multi-target
copies by `(display string, first source location)`.

### 4.5 `SymbolSearchService`

`SearchSymbolsAsync`, `FindReferencesAsync`, `GetSymbolInfoAsync`,
`FindImplementationsAsync`, `FindCallersAsync`; all take a `CancellationToken` and pass
it to every Roslyn call. Uses `SymbolResolver` for name lookup.

### 4.6 `SecurityValidator`

`ValidateSolutionPath`, `ValidateSourceFilePath` unchanged. `SanitizeSearchPattern` is
replaced by `ValidateSearchPattern(string) → string?` returning an error message when
the pattern is empty/whitespace or longer than 256 characters, otherwise null. The
pattern itself is passed through untouched (it is only ever `Regex.Escape`d).

### 4.7 `Formatting/ResultFormatter`

Static class holding the nine `Format*` methods currently in `CodeNavigationTools`,
unchanged in output except where §5 changes the data.

### 4.8 `Tools/CodeNavigationTools`

Ten `[McpServerTool]` methods, all `ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false`,
all taking `CancellationToken`. Each validates its inputs, calls one service method
inside `ExecuteAsync`, and returns one formatter result. `ExecuteAsync` keeps the
current exception mapping (`OperationCanceledException`, `FileNotFoundException`,
`UnauthorizedAccessException`, `ArgumentException`/`NotSupportedException` message
passthrough, catch-all logged with a generic message) and appends the §5.8 hint.

## 5. Behaviour changes

Tool names and parameters are unchanged except `maxDepth → maxNamespaces` (§5.3).

### 5.1 `roslyn_find_references`: `includeDefinition` works

`SymbolFinder.FindReferencesAsync` reports declaration sites in
`ReferencedSymbol.Definition.Locations`, not in `ReferencedSymbol.Locations`, so the
current `isDefinition` test never succeeds. When `includeDefinition` is true, each
source location of `Definition` is added as a `ReferenceResult` with `IsDefinition = true`
and `ReferenceKind = "Definition"`. Deduplication by `(path, line)` keeps the definition
entry over a reference entry on the same line.

### 5.2 `roslyn_search_symbols`: `symbolTypes` distinguishes type kinds

Accepted words: `class`, `interface`, `struct`, `enum`, `record`, `delegate`, `method`,
`property`, `field`, `event`, `namespace`. Type words filter on
`INamedTypeSymbol.TypeKind`: `class` is `TypeKind.Class` (record classes included),
`struct` is `TypeKind.Struct` (record structs included), `record` is any named type
with `IsRecord`. An
unknown word returns `Error: unknown symbol type 'x'. Accepted: ...` without
searching. The parameter description lists the accepted words.

### 5.3 `roslyn_analyze_dependencies`: no overlap with dotnet-mcp

`dotnet_project Dependencies` / `dotnet_package List` already report packages and
project references from MSBuild. This tool returns only what Roslyn knows:

- per project: name, symbol counts (total/public/internal), and the project references
  *as compiled* (names of referenced projects);
- namespace usage: `using` directives aggregated across the solution, top
  `maxNamespaces` (default 20).

The flat list of metadata assembly names is removed. `maxDepth` (which only scaled a
`Take`) is replaced by `maxNamespaces`. The tool description says: "For NuGet packages
and versions use dotnet_package List; for MSBuild-level project info use dotnet_project
Analyze."

### 5.4 Cancellation

`SearchSymbols`, `FindReferences`, `GetSymbolInfo` gain `CancellationToken cancellationToken = default`
and the services pass it to `GetCompilationAsync`, `GetSemanticModelAsync`, `GetTextAsync`
and every `SymbolFinder` call.

### 5.5 Search patterns are not mangled

`SanitizeSearchPattern` stripped `<`, `>`, backticks and other punctuation so
`List<*>` or `Dictionary\`2` could not be searched. Replaced per §4.6.

### 5.6 Qualified names match generic owners

`MatchesName` compares the qualified candidate against both
`ContainingSymbol.ToDisplayString()` (renders `OrderService<T>`) and a metadata-name
form built from `ContainingSymbol.Name` chain (`OrderService`), so `OrderService.Save`
finds `OrderService<T>.Save`. Comparison stays `OrdinalIgnoreCase`.

### 5.7 Cyclomatic complexity

Counted per *method-like* declaration: methods, constructors, operators, and
property/indexer/event accessors with bodies. Lambdas and local functions nested inside
count toward their enclosing declaration (as today) and are not reported separately. Decision points, each +1: `if`, `while`, `for`, `foreach`, `do`,
each `case` label or `case` pattern (not `default`), `catch`, `?:`, `??`, `??=`, `&&`,
`||`, `and`/`or` patterns, each switch-expression arm except a trailing discard `_`,
and each `when` guard. Base complexity 1.
Results: `ComplexityResult { MemberName, ContainingType, Namespace, FilePath, LineNumber, Complexity }`
with `MemberName` as Roslyn's minimal display string (e.g. `Validate(Order)`, `.ctor(int)`,
`Total.get`). Deduped per `(FilePath, LineNumber)` for multi-target projects.

### 5.8 Load warnings are surfaced once

The first tool result after a workspace load that produced MSBuild warnings ends with:
`\n(Workspace loaded with N MSBuild warnings; call roslyn_reload_workspace to see them.)`
Subsequent results on the same cached workspace do not repeat it. `roslyn_reload_workspace`
continues to list the warnings themselves.

### 5.9 Unrestored projects are named as the cause

When a diagnostics result contains more than ten `CS0246`/`CS0234` entries, the list is
prefixed with one synthetic entry `RMCP001` (severity Error, no location):
"Many 'type or namespace not found' errors: packages are probably not restored. Run
dotnet_project Restore." The server instructions already promise this hint in prose;
this puts it where the model is looking.

## 6. Tests

### 6.1 Fixture `tests/fixtures/SampleSolution/`

A real solution, loaded by `MSBuildWorkspace` in tests. It lives outside the test
project's directory, so nothing globs it into the test build; tests reach it by absolute
path via `TestPaths.FixturesDir`, an `AssemblyMetadata` set in the test csproj exactly
like the fork's `FixturesDir`.

```
SampleSolution/
  Sample.slnx
  Directory.Build.props            Nullable, ImplicitUsings, LangVersion latest — no analyzers, no warnings-as-errors
  Sample.Core/Sample.Core.csproj   classlib; TargetFrameworks net10.0;netstandard2.0 so multi-target dedupe is exercised
  Sample.Core/IsExternalInit.cs    empty shim under #if !NET so records compile for netstandard2.0
  Sample.Core/IOrderRepository.cs  interface: Store(Order), Find(int)
  Sample.Core/OrderRepository.cs   implements IOrderRepository
  Sample.Core/OrderServiceBase.cs  abstract; virtual bool Validate(Order)
  Sample.Core/OrderService.cs      OrderService<T> : OrderServiceBase; overrides Validate; Save(T) calls IOrderRepository.Store
  Sample.Core/Order.cs             record Order(int Id, decimal Total, OrderStatus Status)
  Sample.Core/OrderStatus.cs       enum
  Sample.Core/PricingRules.cs      Classify(Order): complexity exactly 9 by §5.7 rules (documented in a comment)
  Sample.Core/Scratch.cs           a method with an unused local → CS0219 at a known line
  Sample.App/Sample.App.csproj     console, net10.0, ProjectReference Sample.Core
  Sample.App/Program.cs            constructs OrderService<Order>, calls Save and Validate
```

Each symbol name appears exactly once in the fixture so tests can target it without
qualification, except `Validate`, which is declared twice on purpose (base + override).

The fixture has no package references, so restore needs only the targeting packs that
ship with the SDK. `MSBuildWorkspace` still needs `obj/project.assets.json` to exist:
`AssemblySetup` runs `dotnet restore` on `Sample.slnx` once when it is missing. Tests that
modify files copy the whole fixture (including `obj/`) to a temporary directory first and
never touch the checked-in copy.

### 6.2 Test project

`tests/Roslyn.Mcp.Tests/Roslyn.Mcp.Tests.csproj`: `Microsoft.NET.Sdk`, `OutputType Exe`,
`IsPackable false`, `EnableMSTestRunner`, `TestingPlatformDotnetTestSupport`,
`NoWarn CA1707;CA1861`, `PackageReference MSTest`, `ProjectReference ../../src/Roslyn.Mcp`,
`AssemblyMetadata FixturesDir` and `ServerExePath` (= `../../src/Roslyn.Mcp/bin/$(Configuration)/net10.0/roslyn-mcp(.exe)`),
global using `Microsoft.VisualStudio.TestTools.UnitTesting`.

`AssemblySetup.cs`: `[AssemblyInitialize]` registers MSBuild once. `MSTestSettings.cs`
enables class-level parallelism. Classes that load the fixture do so once in
`[ClassInitialize]` through a shared `WorkspaceManager` (refresh interval zero) so the
whole suite runs in tens of seconds.

Test names follow `Method_Scenario`.

### 6.3 Coverage

| Class | Covers |
|---|---|
| `Services/WorkspaceManagerTests` | resolve `.sln`/`.slnx`/`.csproj`/directory; ambiguous and empty directories; missing file; edited `.cs` picked up without reload (new text visible, `DocumentId`s unchanged, no second load logged); added `.cs`, changed `.csproj`, touched `obj/project.assets.json` and new `Directory.Build.props` trigger reload; `ReloadAsync` project list; `TryTakeLoadHint` true once then false |
| `Services/SymbolResolverTests` | simple name; `Type.Member`; `Namespace.Type`; generic owner `OrderService.Save`; case-insensitivity; no match returns empty; one result per symbol although `Sample.Core` is multi-targeted |
| `Services/SymbolSearchServiceTests` | `*Service`, `Get*`, `?rder`, `List<*>`-style patterns; each `symbolTypes` word alone yields only that kind; unknown word error; `includeDefinition` true adds the declaration line, false omits it; implementations: interface → class, class → derived, member → override; callers of `Save` found in `Sample.App`; relevance ordering puts exact match first |
| `Services/CodeAnalysisServiceTests` | CS0219 at the known line; `Error` filter hides it; `filePath` scoping; file not in solution → `ArgumentException`; outline depth/kinds/line ranges for `OrderService.cs`; `Classify` complexity == 9 and threshold filtering; dependencies: `Sample.App → Sample.Core`, namespace usage counts, no assembly names |
| `Services/SecurityValidatorTests` | relative path, `..` segment, wrong extension, nonexistent file rejected; directory accepted; pattern empty / 257 chars rejected, `List<*>` accepted |
| `Formatting/ResultFormatterTests` | each formatter on hand-built models: empty, grouping by file, `... and N more`, definition marker, warning hint |
| `Tools/CodeNavigationToolsTests` | `ExecuteAsync` mapping: invalid path message; `ArgumentException` text passthrough; cancellation message; generic message for unexpected exceptions; `minimumSeverity` validation; `symbolTypes` validation |
| `E2E/ServerTests` | start `roslyn-mcp` over stdio with `ModelContextProtocol` client; `tools/list` returns exactly the ten `roslyn_*` tools, each with `readOnlyHint = true`; server instructions contain `dotnet_`; `roslyn_search_symbols` on the fixture returns `OrderService`; `--version` prints the `Directory.Build.props` version. `Assert.Inconclusive` when the exe is not built |

## 7. Documentation, scripts, CI, packaging

### 7.1 README.md

Sections, in order: what it is (two sentences); **How this expands on dotnet-mcp**
(the table of what each server covers; that dotnet-mcp's `Analyze`/`Dependencies`/`Validate`
are MSBuild evaluation while `roslyn_*` is the compiler's semantic model; the loop
locate → check impact → edit → `roslyn_get_diagnostics` → `dotnet_project Build/Test`;
the three combined example prompts; the "stays in sync" bullet list); Prerequisites
(.NET 10 SDK — required at runtime, not just to build); Install (`.mcpb` for Claude
Desktop; from source for everything else); Configure (one JSON block with both `dotnet`
and `roslyn` entries, valid escaping, exe path); Tools table; Troubleshooting (restore,
MSBuild warnings, ambiguous directory); Development (build, test, inspector); License.

### 7.2 AGENTS.md

Layout; `dotnet build Roslyn.Mcp.slnx`, `dotnet test` from the repo root (MTP note);
conventions: warnings as errors, `Method_Scenario` names, fixture is read-only — copy to
temp to mutate, stdio only, no new tools without a spec; the dotnet-mcp split in four
lines; use the dotnet MCP tools with `workingDirectory` at the repo root.

### 7.3 scripts/setup.ps1

`-ConfigPath` (default `%APPDATA%\Claude\claude_desktop_config.json`), `-Configuration`
(default Release), `-SkipBuild`. Builds, verifies `roslyn-mcp --version`, merges
`dotnet` (dnx `Community.Mcp.DotNet@1.* --yes`) and `roslyn` (the built exe) entries into
the config, preserving other servers. No background jobs, no process killing.

### 7.4 packaging/

`manifest.json` follows the current `@anthropic-ai/mcpb` manifest schema (the
implementer takes `manifest_version` and field names from that package's schema, not
from memory): `name roslyn-mcp`, `display_name`, `version ${VERSION}`, `description`,
`author`, `server { type: "binary", entry_point: "server/roslyn-mcp.dll", mcp_config { command: "dotnet", args: ["${__dirname}/server/roslyn-mcp.dll"] } }`,
`tools` list of the ten names, `compatibility { platforms: [win32, darwin, linux] }`,
no `user_config`. The bundle's `long_description` states the .NET 10 SDK prerequisite.

`pack.ps1`: reads `Version` from `Directory.Build.props`; `dotnet publish src/Roslyn.Mcp -c Release --no-self-contained -o <stage>/server`;
writes the substituted manifest; zips to `artifacts/roslyn-mcp-<version>.mcpb`.
Validates with `npx @anthropic-ai/mcpb validate` when `npx` is available, otherwise
skips with a note.

### 7.5 Workflows

`ci.yaml`: on push to main, pull_request, workflow_dispatch; matrix `ubuntu-latest`,
`windows-latest`; `actions/setup-dotnet` with `global-json-file`; `dotnet build Roslyn.Mcp.slnx -c Release`;
`dotnet test Roslyn.Mcp.slnx -c Release --no-build`. There is no version-consistency
job: `Directory.Build.props` is the only place a version is written, `pack.ps1`
substitutes it into the manifest, and the E2E test asserts `--version` prints it.

`build-artifacts.yaml`: on tag `v*` and workflow_dispatch; windows-latest; runs
`pack.ps1`; uploads the `.mcpb` as an artifact; on a tag, attaches it to the release.

### 7.6 .mcp.json

```json
{ "mcpServers": { "roslyn": { "command": "src/Roslyn.Mcp/bin/Debug/net10.0/roslyn-mcp.exe" } } }
```

## 8. Risks and mitigations

- **MSBuildLocator in the test host.** The test project must not have `Microsoft.Build.*`
  assemblies in its output; `ExcludeAssets="runtime"` on the server's
  `Microsoft.Build.Framework` reference prevents that. Verified by the first test that
  loads the fixture.
- **Fixture restore on CI.** The fixture has no package references; `MSBuildWorkspace`
  still needs `obj/project.assets.json`. `[AssemblyInitialize]` runs `dotnet restore`
  on the fixture once if the assets file is missing.
- **Suite time.** Workspace loads are shared per class; the E2E class starts one server.
  Target under 60 s on CI.
- **Analyzer noise from `latest-recommended` + warnings as errors.** The first build
  under the new props will raise rules the current code does not follow (sealing,
  `static` members, logging templates, culture-aware string APIs). Each is either fixed
  in code or suppressed in `Directory.Build.props` with a one-line reason, as the
  fork's test csproj does for CA1707/CA1861. No blanket `NoWarn`.

## 9. Acceptance

- `dotnet build Roslyn.Mcp.slnx -c Release` succeeds with zero warnings.
- `dotnet test Roslyn.Mcp.slnx -c Release --no-build` passes on Windows and Linux.
- `npx @modelcontextprotocol/inspector roslyn-mcp.exe` lists ten `roslyn_` tools.
- `pack.ps1` produces a bundle that `mcpb validate` accepts.
- Running `roslyn_find_references` with `includeDefinition = true` on `OrderService`
  shows a `Definition` line; `roslyn_search_symbols` with `symbolTypes = interface`
  returns only `IOrderRepository`.
- README contains the "How this expands on dotnet-mcp" section.
