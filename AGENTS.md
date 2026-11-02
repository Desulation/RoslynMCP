# AGENTS.md

Roslyn MCP: a read-only C# MCP server (`roslyn-mcp`, server name `roslyn`, tools prefixed `roslyn_`) over stdio. It complements dotnet-mcp; see the README.

## Layout

```
Roslyn.Mcp.slnx                 solution (folders /src/ and /tests/)
global.json                     SDK 10.0.x; test runner Microsoft.Testing.Platform
Directory.Build.props           net10.0, warnings as errors, analyzers, Version (single source)
Directory.Packages.props        central package versions; PackageReferences carry no Version
src/Roslyn.Mcp/                 the server (exe roslyn-mcp)
  Program.cs                    MSBuildLocator, --version, stdio host, server instructions
  Tools/CodeNavigationTools.cs  the ten [McpServerTool] methods
  Services/                     WorkspaceManager, CodeAnalysisService, SymbolResolver,
                                SymbolSearchService, SymbolTypeFilter, ComplexityCalculator,
                                SecurityValidator, WorkspaceOptions
  Formatting/ResultFormatter.cs tool result text
  Models/SearchModels.cs        result models
tests/Roslyn.Mcp.Tests/         MSTest on Microsoft.Testing.Platform (Services/, Formatting/, Tools/, E2E/)
tests/fixtures/SampleSolution/  checked-in sample solution the tests load
scripts/setup.ps1               builds and registers dotnet + roslyn in a client config
docs/superpowers/specs/         design specs
.github/workflows/              ci.yaml (build + test, Ubuntu and Windows); build-artifacts.yaml (.mcpb on v* tags)
```

## Commands

```powershell
dotnet build Roslyn.Mcp.slnx
dotnet test
```

Run both from the repo root: with no arguments, `dotnet test` tests the solution in the current directory. `global.json` selects Microsoft.Testing.Platform, which takes a solution path through `--solution`, not as a positional argument. CI (`.github/workflows/ci.yaml`) runs `dotnet build Roslyn.Mcp.slnx -c Release` and then `dotnet test --solution Roslyn.Mcp.slnx -c Release --no-build`. The E2E tests start `src/Roslyn.Mcp/bin/<Configuration>/net10.0/roslyn-mcp` and are inconclusive if it is not built.

## Conventions

- Warnings are errors (`TreatWarningsAsErrors`, `latest-recommended` analyzers). Fix the code; suppress only in `Directory.Build.props` with a one-line reason.
- Test names are `Method_Scenario`.
- `tests/fixtures/SampleSolution` is read-only. Tests that change files copy it to a temp directory first (`FixtureCopy`).
- Stdio only: stdout carries the protocol, all logging goes to stderr. No HTTP transport.
- No new tools without a spec in `docs/superpowers/specs/`.

## dotnet-mcp split

- dotnet-mcp (`dotnet_*`) changes and runs things: new projects, packages, references, restore, build, test, run, format.
- Roslyn MCP (`roslyn_*`) only reads: symbols, references, implementations, callers, outlines, diagnostics, complexity.
- dotnet-mcp's `Analyze`/`Dependencies`/`Validate` evaluate MSBuild; `roslyn_*` answers from the compiler's semantic model.
- Never duplicate a dotnet-mcp feature here (e.g. NuGet package lists belong to `dotnet_package List`).

## Working in this repo

- Use the dotnet MCP tools (`dotnet_project` Build/Test/Restore, `dotnet_solution`, `dotnet_package`) with `workingDirectory` at the repo root.
- Inspect the server interactively: `npx @modelcontextprotocol/inspector src/Roslyn.Mcp/bin/Debug/net10.0/roslyn-mcp.exe`
