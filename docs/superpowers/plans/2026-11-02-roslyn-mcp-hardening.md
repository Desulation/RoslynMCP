# Roslyn MCP Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn `RoslynMcpServer` into `Roslyn.Mcp`, a tested, stdio-only, packaged MCP server that complements dotnet-mcp with Roslyn's semantic view of C# code.

**Architecture:** One executable project under `src/Roslyn.Mcp` with `Tools → {SymbolSearchService, CodeAnalysisService} → SymbolResolver → WorkspaceManager`; formatters depend only on models. One MSTest project under `tests/Roslyn.Mcp.Tests` that loads a checked-in sample solution through the real `MSBuildWorkspace`, plus an end-to-end test that drives the built exe over stdio with the MCP client.

**Tech Stack:** .NET 10 SDK, C# latest, Microsoft.CodeAnalysis 5.9.0 (`CSharp.Workspaces`, `Workspaces.MSBuild`), Microsoft.Build.Locator 1.11.2, ModelContextProtocol 2.2.0, Microsoft.Extensions.Hosting 10.0.14, MSTest 4.5.2 on Microsoft.Testing.Platform, PowerShell 7 scripts, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-11-02-roslyn-mcp-hardening-design.md`

## Global Constraints

- Every project builds with `TreatWarningsAsErrors=true`, `EnforceCodeStyleInBuild=true`, `AnalysisLevel=latest-recommended`, `Nullable=enable`, `LangVersion=latest`, `InvariantGlobalization=true`, `ManagePackageVersionsCentrally=true` (spec §2.1). No blanket `NoWarn`; each suppression lives in `Directory.Build.props` with a one-line reason (spec §8).
- Package versions live only in `Directory.Packages.props`; `PackageReference` items carry no `Version`.
- Neither project may have `Microsoft.Build.*` assemblies in its build output (spec §2.1): both reference `Microsoft.Build.Framework` with `ExcludeAssets="runtime" PrivateAssets="all"`.
- Executable is `roslyn-mcp`, root namespace `Roslyn.Mcp`, MCP server name `roslyn`, tool prefix `roslyn_` (spec §2.2). Tool names and parameters are unchanged except `maxDepth → maxNamespaces` (spec §5).
- Stdio only. `--stdio` accepted and ignored; `--version` prints the `Version` from `Directory.Build.props` to stdout and exits 0 (spec §3). All logging goes to stderr.
- Test names use `Method_Scenario`; test classes are `<Thing>Tests` in folders mirroring `src` (spec §6.2).
- The checked-in fixture under `tests/fixtures/SampleSolution` is never modified by a test; tests that mutate files copy it to a temp directory first (spec §6.1).
- Commit after every task with a Conventional Commits message; commits end with `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`.
- No new tools, no HTTP, no AOT, no VB.NET (spec §1).

## Review Focus

1. A solution directory passed with a trailing separator or different casing (`C:\Repo\` vs `c:\repo`) must resolve to the same cached workspace, not two. → Task 4 `ResolveSolutionPath_TrailingSeparatorAndCase_SameCacheEntry`.
2. A `.cs` file that is being written while the refresh scan runs (locked for write) must not break the request; the next request picks it up. → Task 4 `GetSolutionAsync_FileLockedDuringRefresh_RetriesNextCall`.
3. A `symbolName` with surrounding whitespace or a trailing `()` (`"Save()"`, `" Save "`) is what models actually send; it must still resolve. → Task 6 `MatchesName_TrimsWhitespaceAndParentheses_Matches`.
4. A wildcard pattern that is only `*` over a real solution must return a result set the formatter caps (20 per category) rather than megabytes. → Task 7 `SearchSymbolsAsync_StarOnly_ReturnsAllDeclaredSymbols` plus Task 12 `SearchResults_MoreThanTwentyInCategory_AppendsAndNMore`.
5. `roslyn_get_diagnostics` on a project whose packages are not restored must say so (CS0246 flood) rather than return an unexplained wall of errors. → Task 11 `GetDiagnosticsAsync_UnrestoredProject_ReportsRestoreHint`.

---

### Task 1: Repository layout and build properties

**Files:**
- Move: `RoslynMcpServer/` → `src/Roslyn.Mcp/` (git mv), `RoslynMcpServer.csproj` → `Roslyn.Mcp.csproj`
- Create: `Roslyn.Mcp.slnx`, `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `nuget.config`, `LICENSE`
- Delete: `RoslynMCP.slnx`, `claude_desktop_config.json`, `src/Roslyn.Mcp/{Dockerfile,test-installation.bat,test-installation.sh,RoslynMcpServer.csproj.user,RoslynMcpServer.http,README.md,.gitignore,Properties/launchSettings.json}`
- Move: `src/Roslyn.Mcp/setup.ps1` → `scripts/setup.ps1` (rewritten in Task 15; moved now so the layout is final)
- Modify: `src/Roslyn.Mcp/Roslyn.Mcp.csproj`, every `.cs` file's `namespace` line

**Interfaces:**
- Produces: the layout and props every later task assumes; namespaces `Roslyn.Mcp.{Models,Services,Tools}`.

- [ ] **Step 1: Move and delete**

```bash
git mv RoslynMcpServer src/Roslyn.Mcp
git mv src/Roslyn.Mcp/RoslynMcpServer.csproj src/Roslyn.Mcp/Roslyn.Mcp.csproj
mkdir scripts && git mv src/Roslyn.Mcp/setup.ps1 scripts/setup.ps1
git rm RoslynMCP.slnx claude_desktop_config.json
git rm src/Roslyn.Mcp/Dockerfile src/Roslyn.Mcp/test-installation.bat src/Roslyn.Mcp/test-installation.sh src/Roslyn.Mcp/RoslynMcpServer.http src/Roslyn.Mcp/README.md src/Roslyn.Mcp/.gitignore src/Roslyn.Mcp/Properties/launchSettings.json
git rm --cached src/Roslyn.Mcp/RoslynMcpServer.csproj.user 2>/dev/null; rm -f src/Roslyn.Mcp/RoslynMcpServer.csproj.user
rm -rf src/Roslyn.Mcp/bin src/Roslyn.Mcp/obj
```

- [ ] **Step 2: Write the root build files**

`global.json`:
```json
{
  "sdk": { "version": "10.0.402", "rollForward": "latestFeature" },
  "test": { "runner": "Microsoft.Testing.Platform" }
}
```

`Directory.Build.props`: the properties from Global Constraints plus `<Version>1.0.0</Version>`. Leave an empty `<!-- Analyzer suppressions: one per line, with the reason. -->` `NoWarn` property for Step 5.

`Directory.Packages.props`:
```xml
<Project>
  <ItemGroup>
    <PackageVersion Include="Microsoft.Build.Framework" Version="17.11.48" />
    <PackageVersion Include="Microsoft.Build.Locator" Version="1.11.2" />
    <PackageVersion Include="Microsoft.CodeAnalysis.CSharp.Workspaces" Version="5.9.0" />
    <PackageVersion Include="Microsoft.CodeAnalysis.Workspaces.MSBuild" Version="5.9.0" />
    <PackageVersion Include="Microsoft.Extensions.Hosting" Version="10.0.14" />
    <PackageVersion Include="ModelContextProtocol" Version="2.2.0" />
    <PackageVersion Include="ModelContextProtocol.AspNetCore" Version="2.2.0" />
    <PackageVersion Include="MSTest" Version="4.5.2" />
  </ItemGroup>
</Project>
```
(`ModelContextProtocol.AspNetCore` is removed in Task 2.)

`nuget.config`: `<clear/>` then nuget.org v3, as in the fork.

`Roslyn.Mcp.slnx`:
```xml
<Solution>
  <Folder Name="/src/">
    <Project Path="src/Roslyn.Mcp/Roslyn.Mcp.csproj" />
  </Folder>
  <Folder Name="/tests/" />
</Solution>
```

`LICENSE`: MIT, copyright 2026 Aaron Diehl.

- [ ] **Step 3: Rewrite `src/Roslyn.Mcp/Roslyn.Mcp.csproj`**

Keep `Microsoft.NET.Sdk.Web` for this task (Task 2 switches it). Properties: `OutputType Exe`, `AssemblyName roslyn-mcp`, `RootNamespace Roslyn.Mcp`, `<InternalsVisibleTo Include="Roslyn.Mcp.Tests" />`. Package references without versions: `Microsoft.Build.Framework` (`ExcludeAssets="runtime" PrivateAssets="all"`), `Microsoft.CodeAnalysis.CSharp.Workspaces`, `Microsoft.CodeAnalysis.Workspaces.MSBuild`, `Microsoft.Build.Locator`, `ModelContextProtocol`, `ModelContextProtocol.AspNetCore`. Drop `Microsoft.CodeAnalysis.CSharp` (transitive) and `Microsoft.Extensions.Caching.StackExchangeRedis`. Remove `TargetFramework`/`Nullable`/`ImplicitUsings` (now in props).

- [ ] **Step 4: Rename namespaces**

In every `.cs` under `src/Roslyn.Mcp`: `RoslynMcpServer` → `Roslyn.Mcp` (namespace declarations, `using RoslynMcpServer.*`, the `SymbolInfo` alias in `SymbolSearchService.cs`, `typeof(Program)` stays).

- [ ] **Step 5: Build and clear analyzer diagnostics**

Run: `dotnet build Roslyn.Mcp.slnx -c Release`
Expected: errors from `latest-recommended` + warnings-as-errors. For each distinct rule: fix in code when the fix is local (seal classes, make members static, add `StringComparison`, `CultureInfo.InvariantCulture`); otherwise add the id to `NoWarn` in `Directory.Build.props` with a comment. Likely candidates: CA1848 (LoggerMessage delegates — suppress: a handful of log calls, no hot path), CA1515 (make public types internal — suppress: tool/service types are discovered by the MCP SDK and tested), CA1031 (catch general exception — suppress: `ExecuteAsync` deliberately maps all exceptions to a safe message). Repeat until the build is clean.

Run: `dotnet build Roslyn.Mcp.slnx -c Release`
Expected: `0 Warning(s)`, `0 Error(s)`

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "refactor: move server to src/Roslyn.Mcp with shared build props"
```

---

### Task 2: Stdio-only startup and dead-code removal

**Files:**
- Modify: `src/Roslyn.Mcp/Roslyn.Mcp.csproj`, `src/Roslyn.Mcp/Program.cs`, `src/Roslyn.Mcp/Services/SymbolSearchService.cs`, `src/Roslyn.Mcp/Models/SearchModels.cs`, `Directory.Packages.props`
- Create: `src/Roslyn.Mcp/ServerVersion.cs`
- Delete: `src/Roslyn.Mcp/Services/IncrementalAnalyzer.cs`, `src/Roslyn.Mcp/Services/CacheManager.cs`, `src/Roslyn.Mcp/Services/DiagnosticLogger.cs`

**Interfaces:**
- Produces: `internal static class ServerVersion { public static string Value { get; } }` — `AssemblyInformationalVersion` with any `+suffix` removed (so SourceLink's commit hash never leaks into `--version`).
- Produces: `Program.Main(string[] args)` returning `Task<int>`; DI registrations `WorkspaceOptions`… are added by Task 4; this task registers `CodeAnalysisService`, `SymbolSearchService`, `SecurityValidator` only.

- [ ] **Step 1: Delete dead code**

`git rm` the three service files. In `Models/SearchModels.cs` remove `AnalysisResult`, `NamespaceUsage.UsedTypes`, `ProjectDependency.Version`. In `SymbolSearchService` remove the `IMemoryCache` field, constructor parameter and `using Microsoft.Extensions.Caching.Memory`.

- [ ] **Step 2: Switch SDK and packages**

`Roslyn.Mcp.csproj`: `Sdk="Microsoft.NET.Sdk"`; replace `ModelContextProtocol.AspNetCore` with `Microsoft.Extensions.Hosting`. Remove `ModelContextProtocol.AspNetCore` from `Directory.Packages.props`.

- [ ] **Step 3: Write `ServerVersion.cs` and rewrite `Program.cs`**

`Program.Main`:
1. If `args` contains `--version`: `Console.WriteLine(ServerVersion.Value)`; return 0.
2. `MSBuildLocator.RegisterDefaults()` unless `IsRegistered`; on exception write the message to stderr and return 1.
3. `Host.CreateApplicationBuilder(args.Where(a => !a.Equals("--stdio", OrdinalIgnoreCase)).ToArray())`.
4. `builder.Logging.ClearProviders(); builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);`
5. Register the three services as singletons.
6. `builder.Services.AddMcpServer(o => { o.ServerInfo = new Implementation { Name = "roslyn", Title = "Roslyn C# Code Analysis", Version = ServerVersion.Value }; o.ServerInstructions = ServerInstructions; }).WithStdioServerTransport().WithToolsFromAssembly();`
7. `await builder.Build().RunAsync(); return 0;`

`ServerInstructions` keeps the current text; change the last paragraph's dependency wording to: "roslyn_analyze_dependencies reports symbol counts, the compiled project graph and namespace usage; for NuGet packages use dotnet_package List."

- [ ] **Step 4: Build**

Run: `dotnet build Roslyn.Mcp.slnx -c Release`
Expected: clean. Then `src/Roslyn.Mcp/bin/Release/net10.0/roslyn-mcp.exe --version` prints `1.0.0`.

- [ ] **Step 5: Smoke the protocol**

Run: `npx @modelcontextprotocol/inspector --cli src/Roslyn.Mcp/bin/Release/net10.0/roslyn-mcp.exe --method tools/list`
Expected: JSON listing ten tools whose names start with `roslyn_`. (If `npx` is unavailable, skip; Task 14 covers this.)

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "refactor: stdio-only host, remove unused caching and logging services"
```

---

### Task 3: Test project and sample-solution fixture

**Files:**
- Create: `tests/Roslyn.Mcp.Tests/Roslyn.Mcp.Tests.csproj`, `tests/Roslyn.Mcp.Tests/MSTestSettings.cs`, `tests/Roslyn.Mcp.Tests/TestPaths.cs`, `tests/Roslyn.Mcp.Tests/AssemblySetup.cs`, `tests/Roslyn.Mcp.Tests/DotnetCli.cs`, `tests/Roslyn.Mcp.Tests/FixtureCopy.cs`, `tests/Roslyn.Mcp.Tests/Services/FixtureSmokeTests.cs`
- Create: `tests/fixtures/SampleSolution/**` (listed below)
- Modify: `Roslyn.Mcp.slnx`, `.gitignore`

**Interfaces:**
- Produces: `static class TestPaths { string FixturesDir; string SampleSolutionDir; string SampleSolutionPath /* Sample.slnx */; string ServerExePath; string CoreFile(string name) /* Sample.Core/<name> */; }` — read from `AssemblyMetadata` `FixturesDir` and `ServerExePath`.
- Produces: `static class DotnetCli { static void Restore(string projectOrSolution, bool ignoreFailure = false); }` — runs `dotnet restore`, throws `InvalidOperationException` with stdout+stderr on non-zero exit unless `ignoreFailure`.
- Produces: `sealed class FixtureCopy : IDisposable { static FixtureCopy Create(); string Directory; string SolutionPath; string CoreFile(string name); string AppFile(string name); }` — copies `SampleSolution` (including `obj/`) to a fresh temp directory and runs `DotnetCli.Restore` on it; `Dispose` deletes it.

- [ ] **Step 1: Write the fixture**

`tests/fixtures/SampleSolution/Sample.slnx`:
```xml
<Solution>
  <Project Path="Sample.Core/Sample.Core.csproj" />
  <Project Path="Sample.App/Sample.App.csproj" />
</Solution>
```

`tests/fixtures/SampleSolution/Directory.Build.props` (shields the fixture from the repo root props; nearest file wins):
```xml
<Project>
  <PropertyGroup>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
  </PropertyGroup>
</Project>
```

`Sample.Core/Sample.Core.csproj`: `Microsoft.NET.Sdk`, `<TargetFrameworks>net10.0;netstandard2.0</TargetFrameworks>`.
`Sample.App/Sample.App.csproj`: `Microsoft.NET.Sdk`, `OutputType Exe`, `TargetFramework net10.0`, `ProjectReference ../Sample.Core/Sample.Core.csproj`.

Source files, all `namespace Sample.Core;` (file-scoped) unless noted. Line numbers matter only where stated.

`Sample.Core/IsExternalInit.cs`:
```csharp
#if !NET
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
#endif
```

`Sample.Core/OrderStatus.cs`: `public enum OrderStatus { New, Paid, Cancelled }`

`Sample.Core/Order.cs`: `public record Order(int Id, decimal Total, OrderStatus Status, string? Note = null);`

`Sample.Core/IOrderRepository.cs`: `public interface IOrderRepository { void Store(Order order); Order? Find(int id); }`

`Sample.Core/OrderRepository.cs`: `public class OrderRepository : IOrderRepository` backed by `Dictionary<int, Order>` (`using System.Collections.Generic;`), `Store` indexes by `Id`, `Find` uses `TryGetValue`.

`Sample.Core/OrderServiceBase.cs`: `public abstract class OrderServiceBase { public virtual bool Validate(Order order) => order.Total >= 0; }`

`Sample.Core/OrderService.cs` (`using System;`):
```csharp
public class OrderService<T> : OrderServiceBase where T : Order
{
    private readonly IOrderRepository _repository;

    public OrderService(IOrderRepository repository)
    {
        _repository = repository;
    }

    public int SavedCount { get; private set; }

    public override bool Validate(Order order) => base.Validate(order) && order.Id > 0;

    public void Save(T order)
    {
        if (!Validate(order))
        {
            throw new ArgumentException("Invalid order", nameof(order));
        }

        _repository.Store(order);
        SavedCount++;
    }
}
```

`Sample.Core/PricingRules.cs` (`using System;`), complexity of `Classify` is exactly 9 by spec §5.7 — the comment stays in the file:
```csharp
public static class PricingRules
{
    // Cyclomatic complexity 9: base 1, if +1, if +1, && +1, case +1, case +1, when +1, ?: +1, ?? +1.
    public static string Classify(Order order)
    {
        if (order is null)
        {
            throw new ArgumentNullException(nameof(order));
        }

        if (order.Total > 1000 && order.Status == OrderStatus.Paid)
        {
            return "vip";
        }

        switch (order.Status)
        {
            case OrderStatus.New:
                return "new";
            case OrderStatus.Cancelled when order.Total == 0:
                return "void";
            default:
                break;
        }

        var note = order.Note ?? "none";
        return order.Total > 100 ? "regular:" + note : "small:" + note;
    }
}
```

`Sample.Core/Scratch.cs` — CS0219 must be on **line 7, column 13**:
```csharp
namespace Sample.Core;

public static class Scratch
{
    public static int Unused()
    {
        int unused = 42;
        return 0;
    }
}
```

`Sample.App/Program.cs` (`namespace Sample.App;`, `using System; using Sample.Core;`): `public static class Program { public static void Main() { ... } }` that constructs `new OrderService<Order>(new OrderRepository())`, builds `new Order(1, 250m, OrderStatus.Paid)`, calls `service.Validate(order)` inside an `if`, calls `service.Save(order)`, and writes `PricingRules.Classify(order)` to the console.

Add `tests/fixtures/**/bin/` and `tests/fixtures/**/obj/` to `.gitignore` (already covered by the `[Bb]in/`/`[Oo]bj/` patterns — verify with `git status` after a restore).

- [ ] **Step 2: Verify the fixture builds on its own**

Run: `dotnet build tests/fixtures/SampleSolution/Sample.slnx`
Expected: succeeds with exactly one warning, CS0219 in `Scratch.cs(7,13)`.

- [ ] **Step 3: Write the test project**

`Roslyn.Mcp.Tests.csproj`: mirror the fork — `Microsoft.NET.Sdk`, `OutputType Exe`, `IsPackable false`, `EnableMSTestRunner true`, `TestingPlatformDotnetTestSupport true`, `<NoWarn>$(NoWarn);CA1707;CA1861</NoWarn>` with the fork's comment; `PackageReference MSTest`; `PackageReference Microsoft.Build.Framework` with `ExcludeAssets="runtime" PrivateAssets="all"`; `ProjectReference ../../src/Roslyn.Mcp/Roslyn.Mcp.csproj`; `<Using Include="Microsoft.VisualStudio.TestTools.UnitTesting" />`; and

```xml
<PropertyGroup>
  <ExeSuffix Condition="$([MSBuild]::IsOSPlatform('Windows'))">.exe</ExeSuffix>
</PropertyGroup>
<ItemGroup>
  <AssemblyMetadata Include="FixturesDir" Value="$(MSBuildThisFileDirectory)../fixtures" />
  <AssemblyMetadata Include="ServerExePath" Value="$(MSBuildThisFileDirectory)../../src/Roslyn.Mcp/bin/$(Configuration)/net10.0/roslyn-mcp$(ExeSuffix)" />
</ItemGroup>
```

`MSTestSettings.cs`: `[assembly: Parallelize(Scope = ExecutionScope.ClassLevel)]`.

`AssemblySetup.cs`: `[TestClass] public sealed class AssemblySetup` with `[AssemblyInitialize] public static void Initialize(TestContext _)` that registers `MSBuildLocator` if not registered and calls `DotnetCli.Restore(TestPaths.SampleSolutionPath)` when `Sample.Core/obj/project.assets.json` is missing. This method must not mention any `Microsoft.CodeAnalysis` or `Microsoft.Build` type other than `MSBuildLocator`.

Add the test project to `Roslyn.Mcp.slnx` under `/tests/`.

- [ ] **Step 4: Write the smoke test**

`Services/FixtureSmokeTests.cs`:
```csharp
[TestMethod]
public async Task GetSolutionAsync_SampleSolution_LoadsThreeProjects()
{
    using var service = new CodeAnalysisService(NullLogger<CodeAnalysisService>.Instance);
    var solution = await service.GetSolutionAsync(TestPaths.SampleSolutionPath);
    CollectionAssert.AreEquivalent(
        new[] { "Sample.App", "Sample.Core(net10.0)", "Sample.Core(netstandard2.0)" },
        solution.Projects.Select(p => p.Name).ToArray());
}

[TestMethod]
public void TestOutput_ContainsNoMicrosoftBuildAssemblies()
{
    var dir = Path.GetDirectoryName(typeof(FixtureSmokeTests).Assembly.Location)!;
    var offenders = Directory.GetFiles(dir, "Microsoft.Build*.dll").Select(Path.GetFileName).ToArray();
    CollectionAssert.AreEquivalent(new[] { "Microsoft.Build.Locator.dll" }, offenders);
}
```

- [ ] **Step 5: Run**

Run: `dotnet test Roslyn.Mcp.slnx`
Expected: 2 passed. If `GetSolutionAsync_…` fails with a workspace diagnostic about `project.assets.json`, `AssemblySetup`'s restore did not run — fix before continuing.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "test: add MSTest project and SampleSolution fixture"
```

---

### Task 4: Extract `WorkspaceManager`

**Files:**
- Create: `src/Roslyn.Mcp/Services/WorkspaceManager.cs`, `src/Roslyn.Mcp/Services/WorkspaceOptions.cs`
- Modify: `src/Roslyn.Mcp/Services/CodeAnalysisService.cs`, `src/Roslyn.Mcp/Services/SymbolSearchService.cs`, `src/Roslyn.Mcp/Tools/CodeNavigationTools.cs` (reload tool), `src/Roslyn.Mcp/Program.cs`
- Test: `tests/Roslyn.Mcp.Tests/Services/WorkspaceManagerTests.cs`; update `FixtureSmokeTests` to construct `WorkspaceManager`

**Interfaces:**
- Produces:
  ```csharp
  public sealed class WorkspaceOptions { public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(2); }
  public sealed class WorkspaceManager : IDisposable
  {
      public WorkspaceManager(ILogger<WorkspaceManager> logger, WorkspaceOptions options);
      public static string ResolveSolutionPath(string solutionPath);
      public Task<Solution> GetSolutionAsync(string solutionPath, CancellationToken cancellationToken = default);
      public Task<WorkspaceSummary> ReloadAsync(string solutionPath, CancellationToken cancellationToken = default);
      public bool TryTakeLoadHint(string solutionPath, out int warningCount);
      public int LoadCount { get; }   // full MSBuild loads performed so far
      public void Dispose();
  }
  ```
- `CodeAnalysisService(ILogger<CodeAnalysisService>, WorkspaceManager)` and `SymbolSearchService(ILogger<SymbolSearchService>, WorkspaceManager)` take the manager; `CodeAnalysisService` no longer implements `IDisposable` and loses `GetSolutionAsync`/`ReloadAsync`/`ResolveSolutionPath`.

- [ ] **Step 1: Write the failing tests**

`WorkspaceManagerTests` with a `[ClassInitialize]` that creates one `WorkspaceManager` over the checked-in fixture with `RefreshInterval = TimeSpan.Zero`, and per-test `FixtureCopy` instances for anything that writes:

- `ResolveSolutionPath_SlnxFile_ReturnsFullPath`
- `ResolveSolutionPath_CsprojFile_ReturnsFullPath`
- `ResolveSolutionPath_DirectoryWithOneSolution_ReturnsIt`
- `ResolveSolutionPath_DirectoryWithOnlyProject_ReturnsProject` (point at `Sample.Core/`)
- `ResolveSolutionPath_DirectoryWithTwoSolutions_Throws` → `ArgumentException` whose message contains both file names (copy fixture, add `Other.slnx`)
- `ResolveSolutionPath_EmptyDirectory_Throws` → `ArgumentException`
- `ResolveSolutionPath_MissingFile_ThrowsFileNotFound`
- `ResolveSolutionPath_TrailingSeparatorAndCase_SameCacheEntry` → load via `dir + "\\"` and via the upper-cased dir; `LoadCount == 1`
- `GetSolutionAsync_SecondCall_DoesNotReload` → `LoadCount` stays 1
- `GetSolutionAsync_EditedSourceFile_NewTextVisibleWithoutReload` → append `// edited` to `Order.cs` in a copy; next `GetSolutionAsync` document text ends with it; `LoadCount == 1`; `DocumentId` for `Order.cs` unchanged
- `GetSolutionAsync_AddedSourceFile_Reloads` → write `Extra.cs`; `LoadCount == 2`; solution has a document named `Extra.cs`
- `GetSolutionAsync_ChangedProjectFile_Reloads` → touch `Sample.App.csproj` write time; `LoadCount == 2`
- `GetSolutionAsync_TouchedAssetsFile_Reloads` → touch `Sample.Core/obj/project.assets.json`; `LoadCount == 2`
- `GetSolutionAsync_NewDirectoryBuildProps_Reloads` → write `Sample.Core/Directory.Build.props`; `LoadCount == 2`
- `GetSolutionAsync_FileLockedDuringRefresh_RetriesNextCall` → open `Order.cs` with `FileShare.None` after changing its write time, call `GetSolutionAsync` (no throw, old text), close, call again (new text)
- `ReloadAsync_SampleSolution_ListsProjects` → `ProjectCount == 3`, `Projects` sorted, `DocumentCount > 0`
- `TryTakeLoadHint_CleanSolution_False`
- `TryTakeLoadHint_SolutionWithLoadWarnings_TrueOnceThenFalse` → copy fixture, add `<ProjectReference Include="../Nope/Nope.csproj" />` to `Sample.App.csproj`, `DotnetCli.Restore(copy, ignoreFailure: true)`, load; first call true with `warningCount >= 1`, second false; after `ReloadAsync`, true again

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Roslyn.Mcp.slnx --filter WorkspaceManagerTests`
Expected: build error, `WorkspaceManager` not found.

- [ ] **Step 3: Extract**

Move `ResolveSolutionPath`, `GetSolutionAsync`, `ReloadAsync`, `GetWorkspaceAsync`, `LoadAsync`, `TryRefresh`, `SnapshotProjectFiles`, `ScanSourceFiles`, `CachedWorkspace`, `PathComparer`, `IgnoredDirectories`, `ImplicitImportFiles`, `Dispose` and the two dictionaries from `CodeAnalysisService` into `WorkspaceManager` unchanged, then:
- `RefreshInterval` comes from `options.RefreshInterval`.
- `ResolveSolutionPath` normalises with `Path.TrimEndingDirectorySeparator(Path.GetFullPath(...))` so the cache key is stable; the dictionaries already use `PathComparer`.
- `CachedWorkspace` gains `public bool HintPending { get; set; }` set to `LoadWarnings.Count > 0` on load; `TryTakeLoadHint` resolves the path, takes the per-path gate, returns and clears `HintPending`.
- `LoadCount` increments inside `LoadAsync`.
Update the two services, the reload tool and `Program.cs` (`AddSingleton<WorkspaceOptions>()`, `AddSingleton<WorkspaceManager>()`).

- [ ] **Step 4: Run**

Run: `dotnet test Roslyn.Mcp.slnx`
Expected: all pass. `GetSolutionAsync_FileLockedDuringRefresh_RetriesNextCall` passes only if the `IOException` branch in `TryRefresh` keeps the previous stamp, which it already does.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "refactor: extract WorkspaceManager with injectable refresh interval"
```

---

### Task 5: `SecurityValidator.ValidateSearchPattern`

**Files:**
- Modify: `src/Roslyn.Mcp/Services/SecurityValidator.cs`, `src/Roslyn.Mcp/Tools/CodeNavigationTools.cs:40-47`
- Test: `tests/Roslyn.Mcp.Tests/Services/SecurityValidatorTests.cs`

**Interfaces:**
- Produces: `public string? ValidateSearchPattern(string pattern)` — null when valid; otherwise `"Search pattern must not be empty."` or `"Search pattern must be 256 characters or fewer."`. `SanitizeSearchPattern` is removed.

- [ ] **Step 1: Write the failing tests**

- `ValidateSolutionPath_RelativePath_False`, `ValidateSolutionPath_ParentSegment_False` (`C:\a\..\b.sln`), `ValidateSolutionPath_WrongExtension_False` (an existing `.cs`), `ValidateSolutionPath_MissingFile_False`, `ValidateSolutionPath_ExistingDirectory_True`, `ValidateSolutionPath_ExistingSlnx_True`
- `ValidateSourceFilePath_ExistingCs_True`, `ValidateSourceFilePath_Csproj_False`
- `ValidateSearchPattern_Empty_ReturnsMessage`, `ValidateSearchPattern_Whitespace_ReturnsMessage`, `ValidateSearchPattern_257Chars_ReturnsMessage`, `ValidateSearchPattern_256Chars_Null`, `ValidateSearchPattern_GenericSyntax_Null` (`List<*>`), `ValidateSearchPattern_Backtick_Null` (``Dictionary`2``)

- [ ] **Step 2: Run to verify they fail** — compile error on `ValidateSearchPattern`.

- [ ] **Step 3: Implement** `ValidateSearchPattern`; delete `SanitizeSearchPattern`; in `SearchSymbols` return `"Error: " + message` when non-null and pass `pattern` through untouched.

- [ ] **Step 4: Run** `dotnet test Roslyn.Mcp.slnx --filter SecurityValidatorTests` — all pass.

- [ ] **Step 5: Commit** — `fix: stop sanitising search patterns; validate length instead`

---

### Task 6: Extract `SymbolResolver` and fix generic-owner matching

**Files:**
- Create: `src/Roslyn.Mcp/Services/SymbolResolver.cs`
- Modify: `src/Roslyn.Mcp/Services/SymbolSearchService.cs`, `src/Roslyn.Mcp/Program.cs`
- Test: `tests/Roslyn.Mcp.Tests/Services/SymbolResolverTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed class SymbolResolver
  {
      public SymbolResolver(WorkspaceManager workspaces);
      public Task<List<ISymbol>> FindByNameAsync(string solutionPath, string symbolName, CancellationToken cancellationToken = default);
      public static bool MatchesName(ISymbol symbol, string symbolName);
      public static IEnumerable<ISymbol> EnumerateDeclaredSymbols(Compilation compilation); // GetAllSymbolsRecursive + GetTypeMembersRecursive, moved here
  }
  ```
- `SymbolSearchService(ILogger<SymbolSearchService>, WorkspaceManager, SymbolResolver)`.

- [ ] **Step 1: Write the failing tests** (one resolver over the checked-in fixture per class)

- `FindByNameAsync_SimpleName_FindsType` (`OrderService` → one `INamedTypeSymbol`)
- `FindByNameAsync_TypeDotMember_FindsMember` (`OrderRepository.Store`)
- `FindByNameAsync_NamespaceDotType_FindsType` (`Sample.Core.Order`)
- `FindByNameAsync_GenericOwner_FindsMember` (`OrderService.Save` → one `IMethodSymbol` whose `ContainingType.Name == "OrderService"`)
- `FindByNameAsync_DifferentCase_Finds` (`orderservice`)
- `FindByNameAsync_Unknown_Empty`
- `FindByNameAsync_MultiTargetedProject_OneResultPerSymbol` (`Order` → exactly 1 although `Sample.Core` has two target frameworks)
- `FindByNameAsync_Validate_FindsBaseAndOverride` → 2 results
- `MatchesName_TrimsWhitespaceAndParentheses_Matches` → `" Save() "` matches a symbol named `Save`
- `MatchesName_AccessorsExcluded` → `get_SavedCount` is not returned by `EnumerateDeclaredSymbols`

- [ ] **Step 2: Run to verify they fail** — compile error.

- [ ] **Step 3: Implement**

Move `FindSymbolsByNameAsync`, `MatchesName`, `FormatLocation`, `GetAllSymbolsRecursive`, `GetTypeMembersRecursive` into `SymbolResolver`. `MatchesName`: trim the query, strip one trailing `()`, then match the simple name, or when the query contains `.`: `qualified = ContainingSymbol.ToDisplayString() + "." + Name` **or** `metadataQualified = string.Join(".", containing symbols' Name from outermost non-global to innermost) + "." + Name`; equal to or ending with `"." + query`, `OrdinalIgnoreCase`. Register `SymbolResolver` in `Program.cs`.

- [ ] **Step 4: Run** `dotnet test Roslyn.Mcp.slnx` — all pass.

- [ ] **Step 5: Commit** — `refactor: extract SymbolResolver; match members of generic types by qualified name`

---

### Task 7: `SymbolTypeFilter` and symbol search

**Files:**
- Create: `src/Roslyn.Mcp/Services/SymbolTypeFilter.cs`
- Modify: `src/Roslyn.Mcp/Services/SymbolSearchService.cs` (`SearchSymbolsAsync`, `SearchProjectSymbolsAsync`, `ParseSymbolTypes` removed), `src/Roslyn.Mcp/Tools/CodeNavigationTools.cs` (`SearchSymbols`)
- Test: `tests/Roslyn.Mcp.Tests/Services/SymbolTypeFilterTests.cs`, `tests/Roslyn.Mcp.Tests/Services/SymbolSearchServiceTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed class SymbolTypeFilter
  {
      public static readonly IReadOnlyList<string> AcceptedWords; // class, interface, struct, enum, record, delegate, method, property, field, event, namespace
      public static SymbolTypeFilter Parse(string symbolTypes); // ArgumentException: "unknown symbol type 'x'. Accepted: class, interface, …"
      public bool Matches(ISymbol symbol);
  }
  ```
- `SymbolSearchService.SearchSymbolsAsync(string pattern, string solutionPath, SymbolTypeFilter filter, bool ignoreCase, CancellationToken cancellationToken)` returning `IReadOnlyList<SymbolSearchResult>`.
- Tool `SearchSymbols` gains `CancellationToken cancellationToken = default` as its last parameter; its `symbolTypes` description becomes `"Comma-separated symbol kinds: class, interface, struct, enum, record, delegate, method, property, field, event, namespace"`.

- [ ] **Step 1: Write the failing tests**

`SymbolTypeFilterTests` (parse C# snippets with `CSharpCompilation.Create` — no workspace):
- `Parse_Unknown_ThrowsWithAcceptedList`
- `Parse_MixedCaseAndSpaces_Accepts` (`" Class , METHOD "`)
- `Matches_Class_ExcludesInterfaceStructEnum`
- `Matches_Interface_OnlyInterfaces`
- `Matches_Record_ClassAndStructRecords`
- `Matches_Class_IncludesRecordClass`
- `Matches_Struct_IncludesRecordStruct`
- `Matches_Delegate_OnlyDelegates`

`SymbolSearchServiceTests` over the checked-in fixture:
- `SearchSymbolsAsync_SuffixWildcard_FindsServiceTypes` (`*Service`, filter `class` → `OrderService`)
- `SearchSymbolsAsync_PrefixWildcard_FindsMethods` (`Val*`, `method` → two `Validate`)
- `SearchSymbolsAsync_QuestionMark_MatchesOneChar` (`?rder`, `class,record` → `Order`)
- `SearchSymbolsAsync_InterfaceOnly_ReturnsIOrderRepository`
- `SearchSymbolsAsync_EnumOnly_ReturnsOrderStatus`
- `SearchSymbolsAsync_PropertyOnly_ReturnsSavedCount`
- `SearchSymbolsAsync_GenericPattern_Matches` (`OrderService<*>` against the display string → found)
- `SearchSymbolsAsync_CaseSensitive_NoMatch` (`orderservice`, `ignoreCase: false` → empty)
- `SearchSymbolsAsync_ExactName_RankedFirst` (`Order*` with `class,record` → first result `Order`)
- `SearchSymbolsAsync_StarOnly_ReturnsAllDeclaredSymbols` (`*` with all words → count ≥ 20 and each `FilePath` under the fixture dir)
- `SearchSymbolsAsync_MultiTargeted_NoDuplicates` (`Order`, `record` → exactly 1)
- `SearchSymbolsAsync_Cancelled_Throws` (pre-cancelled token → `OperationCanceledException`)

- [ ] **Step 2: Run to verify they fail** — compile error.

- [ ] **Step 3: Implement**

`SymbolTypeFilter` holds a `HashSet<SymbolKind>` for non-type words, a `HashSet<TypeKind>` for `class/interface/struct/enum/delegate`, and `bool records`. `Matches`: named types match when `TypeKind` is in the set or (`records && IsRecord`); other symbols match on `SymbolKind`. `SearchSymbolsAsync`: use `SymbolResolver.EnumerateDeclaredSymbols`, dedupe by `(ToDisplayString(), first source location)` as `FindByNameAsync` does, pass the token everywhere. The tool parses the filter before `ExecuteAsync` and returns `"Error: " + ex.Message` on `ArgumentException`.

- [ ] **Step 4: Run** `dotnet test Roslyn.Mcp.slnx` — all pass.

- [ ] **Step 5: Commit** — `fix: symbolTypes distinguishes class/interface/struct/enum/record/delegate`

---

### Task 8: References, symbol info, implementations, callers

**Files:**
- Modify: `src/Roslyn.Mcp/Services/SymbolSearchService.cs` (`FindReferencesAsync`, `CreateReferenceResultAsync`, `GetSymbolInfoAsync`), `src/Roslyn.Mcp/Tools/CodeNavigationTools.cs` (`FindReferences`, `GetSymbolInfo`)
- Test: `tests/Roslyn.Mcp.Tests/Services/SymbolSearchServiceTests.cs` (append)

**Interfaces:**
- `FindReferencesAsync(string symbolName, string solutionPath, bool includeDefinition, CancellationToken cancellationToken)` → `IReadOnlyList<ReferenceResult>`.
- `GetSymbolInfoAsync(string symbolName, string solutionPath, CancellationToken cancellationToken)`.
- Private `Task<ReferenceResult?> CreateReferenceResultAsync(Document document, Location location, ISymbol symbol, bool isDefinition, CancellationToken ct)` used for both reference and definition locations.
- Tools `FindReferences` and `GetSymbolInfo` gain `CancellationToken cancellationToken = default`.

- [ ] **Step 1: Write the failing tests**

- `FindReferencesAsync_IncludeDefinition_HasDefinitionEntry` (`OrderRepository` → one result with `IsDefinition && ReferenceKind == "Definition"` whose `DocumentPath` ends with `OrderRepository.cs`)
- `FindReferencesAsync_ExcludeDefinition_NoDefinitionEntry`
- `FindReferencesAsync_CrossProject_FindsAppUsage` (`OrderService` → a result in `Program.cs`)
- `FindReferencesAsync_SameLineDefinitionAndReference_KeepsDefinition` (a `record` positional parameter such as `Order.Total` appears in its own declaration line; result for that line has `IsDefinition`)
- `GetSymbolInfoAsync_Method_HasParametersAndReturnType` (`Classify` → `Parameters == ["Sample.Core.Order order"]`, `ReturnType == "string"`)
- `GetSymbolInfoAsync_Ambiguous_ListsOtherMatches` (`Validate` → `OtherMatches.Count == 1`)
- `GetSymbolInfoAsync_Unknown_Null`
- `FindImplementationsAsync_Interface_FindsClass` (`IOrderRepository` → `OrderRepository`, relation `implements IOrderRepository`)
- `FindImplementationsAsync_InterfaceMember_FindsImplementation` (`IOrderRepository.Store` → `OrderRepository.Store`)
- `FindImplementationsAsync_BaseClass_FindsDerived` (`OrderServiceBase` → `OrderService<T>`)
- `FindImplementationsAsync_VirtualMember_FindsOverride` (`OrderServiceBase.Validate` → relation `overrides OrderServiceBase.Validate`)
- `FindCallersAsync_Method_FindsCallerInOtherProject` (`OrderService.Save` → caller `Sample.App.Program.Main()` in `Program.cs`)
- `FindCallersAsync_InterfaceMember_FindsCallThroughInterface` (`IOrderRepository.Store` → caller `OrderService<T>.Save`)
- `FindCallersAsync_Type_Empty` (`Order` → empty, types are filtered out)

- [ ] **Step 2: Run to verify they fail** — `_IncludeDefinition_` and the three cancellation signatures fail.

- [ ] **Step 3: Implement**

In `FindReferencesAsync`, for each `ReferencedSymbol`: when `includeDefinition`, add `Definition.Locations.Where(l => l.IsInSource)` via `solution.GetDocument(l.SourceTree)`; then add `Locations`. Dedupe with `GroupBy((path, line))` selecting the entry with `IsDefinition` first. Thread the token through every `await`.

- [ ] **Step 4: Run** — all pass.

- [ ] **Step 5: Commit** — `fix: includeDefinition returns declaration sites; cancellation on all symbol tools`

---

### Task 9: Cyclomatic complexity

**Files:**
- Create: `src/Roslyn.Mcp/Services/ComplexityCalculator.cs`
- Modify: `src/Roslyn.Mcp/Services/CodeAnalysisService.cs` (add `AnalyzeComplexityAsync`), `src/Roslyn.Mcp/Models/SearchModels.cs` (`ComplexityResult`), `src/Roslyn.Mcp/Tools/CodeNavigationTools.cs` (`AnalyzeCodeComplexity`, remove `CalculateCyclomaticComplexity`, `GetContainingClassName`, `GetContainingNamespace`)
- Test: `tests/Roslyn.Mcp.Tests/Services/ComplexityCalculatorTests.cs`, `tests/Roslyn.Mcp.Tests/Services/CodeAnalysisServiceTests.cs` (new file; more tests in Tasks 10–11)

**Interfaces:**
- Produces: `public static class ComplexityCalculator { public static int Calculate(SyntaxNode memberDeclaration); public static bool IsMemberLike(SyntaxNode node); }` — `IsMemberLike` is true for `BaseMethodDeclarationSyntax` and `AccessorDeclarationSyntax` with a `Body` or `ExpressionBody`.
- Produces: `CodeAnalysisService.AnalyzeComplexityAsync(string solutionPath, int threshold, CancellationToken ct)` → `List<ComplexityResult>` ordered by `Complexity` descending.
- `ComplexityResult { string MemberName; string ContainingType; string Namespace; string FilePath; int LineNumber; int Complexity; }` — `MemberName` from `SymbolDisplayFormat(memberOptions: IncludeParameters, parameterOptions: IncludeType, miscellaneousOptions: UseSpecialTypes)`, e.g. `Classify(Order)`, `Total.get`.

- [ ] **Step 1: Write the failing tests**

`ComplexityCalculatorTests` parse a snippet with `CSharpSyntaxTree.ParseText` and take the first member-like node:
- `Calculate_EmptyMethod_One`
- `Calculate_IfElse_Two` (else does not count)
- `Calculate_WhileForForeachDo_Five`
- `Calculate_SwitchStatement_CountsCasesNotDefault` (two cases + default → 3)
- `Calculate_CaseWithWhen_CountsBoth` (one case with `when` → 3)
- `Calculate_SwitchExpression_CountsArmsExceptDiscard` (`x switch { 1 => a, 2 => b, _ => c }` → 3)
- `Calculate_ConditionalAndCoalesce_Three` (`a ? b : c ?? d` → 3)
- `Calculate_CoalesceAssignment_Two`
- `Calculate_LogicalOperators_Three` (`a && b || c`)
- `Calculate_Patterns_AndOrCount` (`x is > 0 and < 10 or 42` → 3)
- `Calculate_Catch_Two`
- `Calculate_NestedLambdaAndLocalFunction_CountTowardEnclosing` (method with a lambda containing an `if` and a local function containing a `while` → 3)
- `Calculate_Constructor_Counted`, `Calculate_ExpressionBodiedAccessor_Counted` (`get => a ? 1 : 2;` → 2)
- `IsMemberLike_AutoProperty_False`

`CodeAnalysisServiceTests`:
- `AnalyzeComplexityAsync_Threshold9_ReturnsClassifyOnly` (`MemberName == "Classify(Order)"`, `ContainingType == "PricingRules"`, `Namespace == "Sample.Core"`, `Complexity == 9`, `FilePath` ends with `PricingRules.cs`)
- `AnalyzeComplexityAsync_Threshold10_Empty`
- `AnalyzeComplexityAsync_MultiTargeted_NoDuplicates` (threshold 1 → each `(FilePath, LineNumber)` once)

- [ ] **Step 2: Run to verify they fail** — compile error.

- [ ] **Step 3: Implement**

`Calculate`: walk `node.DescendantNodes(descendIntoChildren: n => n == node || !(n is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or BaseTypeDeclarationSyntax))` and add 1 for each: `IfStatementSyntax`, `WhileStatementSyntax`, `ForStatementSyntax`, `CommonForEachStatementSyntax`, `DoStatementSyntax`, `CaseSwitchLabelSyntax`, `CasePatternSwitchLabelSyntax`, `CatchClauseSyntax`, `ConditionalExpressionSyntax`, `BinaryExpressionSyntax` of kind `CoalesceExpression`/`LogicalAndExpression`/`LogicalOrExpression`, `AssignmentExpressionSyntax` of kind `CoalesceAssignmentExpression`, `BinaryPatternSyntax`, `SwitchExpressionArmSyntax` whose `Pattern` is not `DiscardPatternSyntax`, `WhenClauseSyntax`. `AnalyzeComplexityAsync`: for each document in each compiling project, `GetSyntaxRootAsync` + `GetSemanticModelAsync`, for each `IsMemberLike` node compute and, when `>= threshold`, `GetDeclaredSymbol` for names; `DistinctBy((FilePath, LineNumber))`.

- [ ] **Step 4: Run** — all pass.

- [ ] **Step 5: Commit** — `feat: complexity counts modern C# constructs and accessors/constructors`

---

### Task 10: Dependency analysis without the MSBuild overlap

**Files:**
- Modify: `src/Roslyn.Mcp/Models/SearchModels.cs`, `src/Roslyn.Mcp/Services/CodeAnalysisService.cs` (`AnalyzeDependenciesAsync`), `src/Roslyn.Mcp/Tools/CodeNavigationTools.cs` (`AnalyzeDependencies`, `FormatDependencyAnalysis`)
- Test: `tests/Roslyn.Mcp.Tests/Services/CodeAnalysisServiceTests.cs` (append)

**Interfaces:**
- Models: replace `DependencyAnalysis`/`ProjectDependency` with
  ```csharp
  public sealed class DependencyAnalysis { string SolutionName; List<ProjectSummary> Projects; List<NamespaceUsage> NamespaceUsages; }
  public sealed class ProjectSummary { string Name; int TotalSymbols; int PublicSymbols; int InternalSymbols; List<string> ProjectReferences; }
  public sealed class NamespaceUsage { string Namespace; int UsageCount; }
  ```
- `AnalyzeDependenciesAsync(string solutionPath, int maxNamespaces, CancellationToken ct)`.
- Tool parameter: `[Description("Maximum number of namespaces to list, most used first")] int maxNamespaces = 20`; description per spec §5.3.

- [ ] **Step 1: Write the failing tests**

- `AnalyzeDependenciesAsync_ProjectGraph_AppReferencesCore` (`Projects` contains `Sample.App` with `ProjectReferences == ["Sample.Core(net10.0)"]`)
- `AnalyzeDependenciesAsync_SymbolCounts_Positive` (every `ProjectSummary.TotalSymbols > 0`, `PublicSymbols <= TotalSymbols`)
- `AnalyzeDependenciesAsync_NamespaceUsage_CountsUsings` (`System` usage ≥ 3; `Sample.Core` present)
- `AnalyzeDependenciesAsync_MaxNamespaces_Caps` (`maxNamespaces: 1` → one entry)
- `AnalyzeDependenciesAsync_NoAssemblyNames` (no `Projects[*].ProjectReferences` entry equals `System.Runtime` or `netstandard`; model has no metadata list)

- [ ] **Step 2: Run to verify they fail** — compile error on `ProjectSummary`.

- [ ] **Step 3: Implement** per spec §5.3; `FormatDependencyAnalysis` prints one block per project (`name — N symbols (P public, I internal); references: a, b`) then `Top namespaces:`.

- [ ] **Step 4: Run** — all pass. Also run the full suite.

- [ ] **Step 5: Commit** — `feat: dependency analysis reports the compiled graph and namespace usage only`

---

### Task 11: Diagnostics and outline tests

**Files:**
- Modify: `src/Roslyn.Mcp/Services/CodeAnalysisService.cs` (`GetDiagnosticsAsync` restore hint only)
- Test: `tests/Roslyn.Mcp.Tests/Services/CodeAnalysisServiceTests.cs` (append)

**Interfaces:**
- `GetDiagnosticsAsync` unchanged in signature. New behaviour: when more than 10 diagnostics have `Id == "CS0246"` (or `CS0234`), insert a synthetic first entry `DiagnosticResult { Id = "RMCP001", Severity = Error, Message = "Many 'type or namespace not found' errors: packages are probably not restored. Run dotnet_project Restore.", ProjectName = <first offending project> }`.

- [ ] **Step 1: Write the failing tests**

- `GetDiagnosticsAsync_WholeSolution_FindsCS0219AtLine7` (`Id == "CS0219"`, `LineNumber == 7`, `ColumnNumber == 13`, `FilePath` ends with `Scratch.cs`, exactly one such entry despite two target frameworks)
- `GetDiagnosticsAsync_ErrorOnly_HidesWarning`
- `GetDiagnosticsAsync_FilePath_ScopesToFile` (`Order.cs` → no `CS0219`)
- `GetDiagnosticsAsync_FileNotInSolution_Throws` (temp `.cs` → `ArgumentException` message contains the path)
- `GetDiagnosticsAsync_UnrestoredProject_ReportsRestoreHint` (copy fixture, add `<PackageReference Include="Newtonsoft.Json" Version="13.0.3" />` and a file `using Newtonsoft.Json; … JsonConvert.SerializeObject(1)` ×12 lines to `Sample.App`, do **not** restore the copy; first result `Id == "RMCP001"`)
- `GetDocumentOutlineAsync_OrderService_KindsAndDepths` (entries in order: `Namespace` depth 0 `Sample.Core`; `Class` depth 1 containing `OrderService<T>`; depth 2: `Field _repository`, `Method OrderService(…)`, `Property SavedCount`, `Method Validate`, `Method Save`; every `StartLine <= EndLine`; class `EndLine` is the file's last code line)
- `GetDocumentOutlineAsync_FileNotInSolution_Throws`

- [ ] **Step 2: Run** — the restore-hint test fails; the rest should pass (they pin existing behaviour; if one fails, fix the code, not the test, unless the test's expectation contradicts the spec).

- [ ] **Step 3: Implement** the hint in `GetDiagnosticsAsync` after the `DistinctBy`.

- [ ] **Step 4: Run** — all pass.

- [ ] **Step 5: Commit** — `test: pin diagnostics and outline behaviour; hint at restore on CS0246 floods`

---

### Task 12: `ResultFormatter`

**Files:**
- Create: `src/Roslyn.Mcp/Formatting/ResultFormatter.cs`
- Modify: `src/Roslyn.Mcp/Tools/CodeNavigationTools.cs` (remove all `Format*`, call the formatter)
- Test: `tests/Roslyn.Mcp.Tests/Formatting/ResultFormatterTests.cs`

**Interfaces:**
- Produces `public static class ResultFormatter` with:
  `SearchResults(IReadOnlyList<SymbolSearchResult>)`, `References(IReadOnlyList<ReferenceResult>)`, `SymbolInfo(SymbolInfo?)`, `Locations(List<SymbolLocationResult>, string title)`, `Outline(string filePath, List<OutlineEntry>)`, `Diagnostics(List<DiagnosticResult>, int maxResults)`, `WorkspaceSummary(WorkspaceSummary)`, `Dependencies(DependencyAnalysis)`, `Complexity(List<ComplexityResult>)` — all returning `string`, output identical to today's methods except for the Task 9/10 model changes.

- [ ] **Step 1: Write the failing tests** (hand-built models, no workspace)

- `SearchResults_Empty_SaysFoundZero`
- `SearchResults_GroupsByCategory_Sorted`
- `SearchResults_MoreThanTwentyInCategory_AppendsAndNMore` (25 → `... and 5 more`)
- `References_DefinitionMarked` (line shows `Definition`)
- `References_GroupedByFileAndSortedByLine`
- `SymbolInfo_Null_SaysNotFound`, `SymbolInfo_OtherMatches_ListsThem`
- `Locations_Empty_SaysNoneFound` (`No callers of 'X' found.`)
- `Outline_IndentsByDepthAndShowsRanges` (`L5-9`, single line `L3`)
- `Diagnostics_CountsBySeverityAndCaps` (3 errors 2 warnings, `maxResults: 2` → `... and 3 more`)
- `WorkspaceSummary_ListsWarnings`
- `Dependencies_PrintsProjectsThenNamespaces`
- `Complexity_SortedDescendingAndCapped`

- [ ] **Step 2: Run to verify they fail** — compile error.

- [ ] **Step 3: Move** the nine methods into `ResultFormatter` (renamed as above), adjust visibility to `public static`, and update the tool class.

- [ ] **Step 4: Run** — all pass.

- [ ] **Step 5: Commit** — `refactor: move result formatting out of the tool class`

---

### Task 13: Thin tools, load hint, tool-level tests

**Files:**
- Modify: `src/Roslyn.Mcp/Tools/CodeNavigationTools.cs`
- Test: `tests/Roslyn.Mcp.Tests/Tools/CodeNavigationToolsTests.cs`

**Interfaces:**
- Every tool method gains a `WorkspaceManager workspaces` DI parameter (before `ILogger`) and ends in `CancellationToken cancellationToken = default`.
- `internal static Task<string> ExecuteAsync(SecurityValidator validator, WorkspaceManager workspaces, ILogger logger, string solutionPath, string operation, Func<Task<string>> action, bool appendLoadHint = true)` — after a successful `action`, when `appendLoadHint && workspaces.TryTakeLoadHint(solutionPath, out var n)` append `"\n(Workspace loaded with {n} MSBuild warnings; call roslyn_reload_workspace to see them.)"`. `ReloadWorkspace` passes `appendLoadHint: false`.

- [ ] **Step 1: Write the failing tests** (call the public static tool methods with real services and `NullLogger`)

- `SearchSymbols_RelativeSolutionPath_ReturnsInvalidPathError` (starts with `Error: Invalid solution path`)
- `SearchSymbols_UnknownSymbolType_ReturnsAcceptedList` (contains `unknown symbol type 'widget'`)
- `SearchSymbols_EmptyPattern_ReturnsPatternError`
- `GetDiagnostics_BadSeverity_ReturnsError` (`minimumSeverity must be Error, Warning or Info`)
- `GetDocumentOutline_FileNotInSolution_PassesMessageThrough` (temp `.cs` → `Error: '<path>' is not a document in the solution.`)
- `FindImplementations_CancelledToken_ReturnsCancelledMessage` (`Error: The find implementations operation was cancelled or timed out.`)
- `AnyTool_SolutionWithLoadWarnings_AppendsHintOnce` (fixture copy from Task 4's broken-reference recipe; first `GetSymbolInfo` result ends with the hint, second does not)
- `ReloadWorkspace_SolutionWithLoadWarnings_ListsWarningsWithoutHint`
- `AllTools_HaveReadOnlyAnnotations` (reflect over `[McpServerTool]` methods: exactly 10, all `ReadOnly && Idempotent && !Destructive && !OpenWorld`, names start with `roslyn_`, last parameter is `CancellationToken`)

- [ ] **Step 2: Run to verify they fail** — the hint tests and the reflection test fail.

- [ ] **Step 3: Implement** the `ExecuteAsync` signature, thread `workspaces` into every tool, add the three missing tokens (Tasks 7–8 added them to the services; this task makes the tool surface uniform).

- [ ] **Step 4: Run** — all pass.

- [ ] **Step 5: Commit** — `feat: surface MSBuild load warnings once; uniform cancellation on all tools`

---

### Task 14: End-to-end over stdio

**Files:**
- Test: `tests/Roslyn.Mcp.Tests/E2E/ServerTests.cs`

**Interfaces:**
- Consumes `TestPaths.ServerExePath`; the `ModelContextProtocol` client types (`StdioClientTransport`, `McpClient.CreateAsync` in 2.2.0 — if the resolved SDK names differ, use its stdio client factory equivalent).

- [ ] **Step 1: Write the tests**

`[ClassInitialize]`: `Assert.Inconclusive` if `ServerExePath` does not exist; otherwise start one client with `StdioClientTransportOptions { Command = exe, Arguments = [] }` and keep it for the class.
- `ToolsList_ExactlyTenRoslynTools_AllReadOnly` (names == the ten in the spec; each `ProtocolTool.Annotations.ReadOnlyHint == true`)
- `Initialize_InstructionsMentionDotnetTools` (`ServerInstructions` contains `dotnet_`)
- `CallTool_SearchSymbols_ReturnsOrderService` (`roslyn_search_symbols` with `pattern = "OrderService"`, `solutionPath = TestPaths.SampleSolutionPath`, `symbolTypes = "class"` → text content contains `OrderService`)
- `Version_PrintsDirectoryBuildPropsVersion` (run the exe with `--version`, compare to `<Version>` parsed from `Directory.Build.props` via `XDocument`)
- `StdioFlag_StillAccepted` (second client started with `Arguments = ["--stdio"]` lists tools)

- [ ] **Step 2: Run** `dotnet test Roslyn.Mcp.slnx --filter ServerTests` — all pass (under 15 s).

- [ ] **Step 3: Commit** — `test: end-to-end stdio tests with the MCP client`

---

### Task 15: README, AGENTS.md, .mcp.json, setup script

**Files:**
- Rewrite: `README.md`, `scripts/setup.ps1`
- Create: `AGENTS.md`, `.mcp.json`

- [ ] **Step 1: Write `README.md`** with the sections and order in spec §7.1. The "How this expands on dotnet-mcp" section must contain: the table (Focus / Changes things / Typical tools) from the current README; one paragraph stating that dotnet-mcp's `dotnet_project Analyze`, `Dependencies` and `Validate` evaluate MSBuild (SDK, frameworks, packages, properties) while every `roslyn_*` tool answers from the compiler's semantic model (symbols, references, implementations, callers, diagnostics, complexity); the five-step loop; the three example prompts; the "stays in sync" bullets. Client config block:
  ```json
  {
    "mcpServers": {
      "dotnet": { "command": "dnx", "args": ["Community.Mcp.DotNet@1.*", "--yes"] },
      "roslyn": { "command": "C:\\path\\to\\RoslynMCP\\src\\Roslyn.Mcp\\bin\\Release\\net10.0\\roslyn-mcp.exe" }
    }
  }
  ```
  Tools table from the current README with the `roslyn_analyze_dependencies` row updated. Prerequisite sentence: ".NET 10 SDK — required to run, not only to build: the server loads your projects with the installed SDK's MSBuild."

- [ ] **Step 2: Write `AGENTS.md`** per spec §7.2 (layout tree, the two commands, the MTP working-directory note, conventions list, dotnet-mcp split in four lines, "use dotnet MCP tools with workingDirectory at the repo root", inspector one-liner).

- [ ] **Step 3: Write `.mcp.json`** per spec §7.6.

- [ ] **Step 4: Rewrite `scripts/setup.ps1`** per spec §7.3: parameters `-ConfigPath`, `-Configuration` (`Release`), `-SkipBuild`; steps: build (`dotnet build Roslyn.Mcp.slnx -c $Configuration`), verify `& $exe --version` prints a version, read/merge/write the JSON (preserve other servers, replace `dotnet` and `roslyn`), print next steps. No background jobs, no `taskkill`.

- [ ] **Step 5: Verify** — `pwsh scripts/setup.ps1 -ConfigPath "$env:TEMP\mcp-test.json"` writes a file with both entries; `README.md` renders (check headings with `Select-String '^#'`).

- [ ] **Step 6: Commit** — `docs: single README with the dotnet-mcp companion section; AGENTS.md; setup script`

---

### Task 16: `.mcpb` packaging

**Files:**
- Create: `packaging/manifest.json`, `packaging/pack.ps1`, `packaging/README.md`
- Modify: `.gitignore` (`artifacts/`, `*.mcpb` — already present from the baseline commit; verify)

- [ ] **Step 1: Get the schema** — `npx @anthropic-ai/mcpb@latest --help` and the package's bundled `manifest.schema.json`; take `manifest_version` and required fields from it.

- [ ] **Step 2: Write `manifest.json`** per spec §7.4 with `"version": "${VERSION}"`, `server.type "binary"`, `entry_point "server/roslyn-mcp.dll"`, `mcp_config.command "dotnet"`, `mcp_config.args ["${__dirname}/server/roslyn-mcp.dll"]`, the ten tool names with one-line descriptions copied from the `[Description]` attributes, `compatibility.platforms ["win32","darwin","linux"]`, and a `long_description` that states the .NET 10 SDK prerequisite.

- [ ] **Step 3: Write `pack.ps1`**: parse `<Version>` from `Directory.Build.props`; `dotnet publish src/Roslyn.Mcp -c Release --no-self-contained -o $stage/server`; copy `manifest.json` with `${VERSION}` substituted and `LICENSE` to `$stage`; `Compress-Archive` to `artifacts/roslyn-mcp-<version>.mcpb` (zip renamed); if `npx` exists run `npx @anthropic-ai/mcpb validate $stage/manifest.json` and fail on non-zero; print the bundle path.

- [ ] **Step 4: Verify** — `pwsh packaging/pack.ps1` produces `artifacts/roslyn-mcp-1.0.0.mcpb`; unzip it to a temp dir and run `dotnet <dir>/server/roslyn-mcp.dll --version` → `1.0.0`.

- [ ] **Step 5: Commit** — `build: framework-dependent .mcpb packaging`

---

### Task 17: CI workflows

**Files:**
- Create: `.github/workflows/ci.yaml`, `.github/workflows/build-artifacts.yaml`

- [ ] **Step 1: Write `ci.yaml`** per spec §7.5: `on: push (main), pull_request, workflow_dispatch`; `permissions: contents: read`; job `test` with `strategy.matrix.os: [ubuntu-latest, windows-latest]`; steps `actions/checkout@v4`, `actions/setup-dotnet@v4` with `global-json-file: global.json`, `dotnet build Roslyn.Mcp.slnx -c Release`, `dotnet test Roslyn.Mcp.slnx -c Release --no-build`.

- [ ] **Step 2: Write `build-artifacts.yaml`**: `on: push tags v*, workflow_dispatch`; `windows-latest`; checkout, setup-dotnet, `actions/setup-node@v4`, `pwsh packaging/pack.ps1`, `actions/upload-artifact@v4` of `artifacts/*.mcpb`; on a tag, `softprops/action-gh-release@v2` attaching the bundle.

- [ ] **Step 3: Verify locally** — `dotnet build Roslyn.Mcp.slnx -c Release` then `dotnet test Roslyn.Mcp.slnx -c Release --no-build` from the repo root pass (the same two commands CI runs); YAML parses (`pwsh -c "ConvertFrom-Yaml"` is unavailable by default — use `npx yaml-lint` or a careful read).

- [ ] **Step 4: Commit** — `ci: build and test on ubuntu and windows; pack .mcpb on tags`

---

## Self-review notes

- Spec coverage: §2 → Tasks 1–3, 15; §3 → Task 2; §4 → Tasks 2, 4, 6, 7, 12, 13; §5.1 → 8; §5.2 → 7; §5.3 → 10; §5.4 → 7, 8, 13; §5.5 → 5; §5.6 → 6; §5.7 → 9; §5.8 → 4, 13; §6 → 3–14; §7.1–7.3, 7.6 → 15; §7.4 → 16; §7.5 → 17; §8 risks → Task 3 (`TestOutput_ContainsNoMicrosoftBuildAssemblies`, restore in `AssemblySetup`), Task 1 Step 5 (suppressions); §9 acceptance → Tasks 14, 16, 8, 7, 15.
- Review Focus 5 added a behaviour the spec implies but did not state (the restore hint as a diagnostic entry, Task 11); the spec's server instructions already promise that hint in prose.
- Type names used across tasks: `WorkspaceManager`, `WorkspaceOptions`, `SymbolResolver`, `SymbolTypeFilter`, `ComplexityCalculator`, `ResultFormatter`, `ProjectSummary`, `ServerVersion`, `TestPaths`, `DotnetCli`, `FixtureCopy` — each defined once in its Interfaces block before use.
