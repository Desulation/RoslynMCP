# Roslyn MCP Server

Roslyn MCP is a read-only MCP (Model Context Protocol) server that gives a model the C# compiler's semantic view of a codebase: symbols, references, implementations, callers, compiler diagnostics and complexity, answered by Roslyn over stdio.
It is built as the companion to the [.NET MCP Server (dotnet-mcp)](https://github.com/jongalloway/dotnet-mcp), which drives the .NET SDK, and works with any MCP client that launches stdio servers, such as Claude Desktop, Claude Code, VS Code and Visual Studio.

## How this expands on dotnet-mcp

The two servers cover different sides of .NET work and do not overlap:

| | dotnet-mcp (`dotnet_*`) | Roslyn MCP (`roslyn_*`) |
|---|---|---|
| Focus | The .NET SDK and CLI | The C# compiler's semantic model |
| Changes things | Yes: new projects, packages, references, EF migrations, format | No, read-only |
| Typical tools | `dotnet_project` (New/Build/Test/Run), `dotnet_package`, `dotnet_solution`, `dotnet_sdk` | `roslyn_search_symbols`, `roslyn_find_references`, `roslyn_find_callers`, `roslyn_get_diagnostics` |

dotnet-mcp's `dotnet_project Analyze`, `Dependencies` and `Validate` evaluate MSBuild: they report the SDK, target frameworks, packages and properties a project file resolves to. Every `roslyn_*` tool instead answers from the compiler's semantic model, so it knows what the code means: which symbols exist, where they are referenced, what implements an interface, who calls a method, which compiler errors a file has and how complex its methods are. Neither server repeats what the other reports.

On connect, this server tells the model to use `dotnet_*` tools to create, build, test and run, and `roslyn_*` tools to understand code. It suggests this loop:

1. **Locate** the code with `roslyn_search_symbols` or `roslyn_get_document_outline`, then read only the relevant line ranges.
2. **Check impact** with `roslyn_find_references`, `roslyn_find_callers` or `roslyn_find_implementations` before changing a signature.
3. **Edit** the files.
4. **Get fast feedback** with `roslyn_get_diagnostics` (pass `filePath` for a single file), without running a build.
5. **Confirm** with `dotnet_project` Build and Test for the authoritative result.

### Example prompts that use both

```
Add Serilog.AspNetCore to the Api project, then find every place we call ILogger.LogError so I can switch them to structured logging.
```
```
Rename IOrderRepository.Save to SaveAsync: show me all implementations and callers first, make the change, then build and run the tests.
```
```
I just edited OrderService.cs. Check it for compile errors, and if it's clean run the unit tests.
```

### How they fit together

- **Tool names are namespaced.** dotnet-mcp prefixes its tools with `dotnet_`; this server uses `roslyn_`, so they never collide and the model can tell them apart.
- **Same paths.** `solutionPath` takes the same solution, project or directory you pass to `dotnet_*` tools.
- **Same transport.** Both servers run over stdio.
- **Stays in sync with dotnet-mcp's changes.** The Roslyn workspace is cached, and at most every 2 seconds a request checks it against the file system:
  - Edited `.cs` files are updated in place, which is cheap.
  - Added or removed `.cs` files, a changed `.sln`/`.slnx`/`.csproj`, `Directory.Build.*`, `Directory.Packages.props` or `global.json`, or a restore (`obj/project.assets.json`) trigger a full reload. That covers `dotnet_package Add`, `dotnet_solution Add`, `dotnet_project New` and `dotnet format`.
  - `roslyn_reload_workspace` forces a reload.

## Prerequisites

- .NET 10 SDK — required to run, not only to build: the server loads your projects with the installed SDK's MSBuild.

## Install

- **Claude Desktop (and other clients that support `.mcpb` bundles):** install `roslyn-mcp-<version>.mcpb`, taken from a GitHub release's assets or built with `pwsh packaging/pack.ps1`. Double-click the file, or drag it onto Claude Desktop's Extensions settings page. The bundle runs the server with your installed `dotnet`, so the .NET 10 SDK must be on PATH. See [packaging/README.md](packaging/README.md).
- **Everything else:** build from source.

```powershell
git clone https://github.com/Desulation/RoslynMCP.git
cd RoslynMCP
dotnet build Roslyn.Mcp.slnx -c Release
```

The server is `src/Roslyn.Mcp/bin/Release/net10.0/roslyn-mcp.exe` (`roslyn-mcp` on macOS and Linux). `roslyn-mcp --version` prints its version.

On Windows, `scripts/setup.ps1` (PowerShell 7) builds the server and adds both the `dotnet` and `roslyn` entries to a client config file, keeping any other servers already in it:

```powershell
pwsh scripts/setup.ps1                                   # Claude Desktop: %APPDATA%\Claude\claude_desktop_config.json
pwsh scripts/setup.ps1 -ConfigPath C:\path\to\mcp.json   # any other client config file
pwsh scripts/setup.ps1 -SkipBuild -Configuration Debug   # register an existing Debug build
```

## Configure

Register both servers in your client:

```json
{
  "mcpServers": {
    "dotnet": { "command": "dnx", "args": ["Community.Mcp.DotNet@1.*", "--yes"] },
    "roslyn": { "command": "C:\\path\\to\\RoslynMCP\\src\\Roslyn.Mcp\\bin\\Release\\net10.0\\roslyn-mcp.exe" }
  }
}
```

- Claude Desktop reads `%APPDATA%\Claude\claude_desktop_config.json` on Windows and `~/Library/Application Support/Claude/claude_desktop_config.json` on macOS. Claude Code's `.mcp.json` uses the same `mcpServers` shape. For other MCP clients that launch stdio servers, check the client's documentation for its config file and key.
- VS Code and Visual Studio use a top-level `servers` key instead of `mcpServers`; the two entries are the same.
- On macOS and Linux, point `command` at `.../net10.0/roslyn-mcp` (no `.exe`).
- The server needs no arguments. `--stdio` is accepted and ignored, so older configs that pass it keep working.

## Tools

| Tool | What it does |
|------|--------------|
| `roslyn_search_symbols` | Wildcard search (`*Service`, `Get*User`, `OrderService<*>`) over symbols declared in source; `symbolTypes` filters by class, interface, struct, enum, record, delegate, method, property, field, event or namespace |
| `roslyn_find_references` | Every reference to a symbol, with the line text; `includeDefinition` adds the declaration sites |
| `roslyn_get_symbol_info` | Kind, signature, accessibility, attributes and declaration location |
| `roslyn_find_implementations` | Implementations of an interface/member, derived classes, overrides |
| `roslyn_find_callers` | The methods, properties and constructors that call a method or access a property or event (one level of the call hierarchy) |
| `roslyn_get_document_outline` | Types and members in a `.cs` file with signatures and line ranges |
| `roslyn_get_diagnostics` | Compiler errors/warnings from the in-memory workspace, no build needed |
| `roslyn_analyze_dependencies` | Per-project symbol counts, the project-reference graph as compiled, and the most used namespaces (`maxNamespaces`, default 20); for NuGet packages use `dotnet_package List` |
| `roslyn_analyze_code_complexity` | Methods, constructors, operators and accessors at or above a cyclomatic complexity threshold |
| `roslyn_reload_workspace` | Drop the cached workspace, reload the solution and list MSBuild load warnings |

All tools are read-only and annotated as such (`readOnlyHint`), so clients can auto-approve them.

Symbol names can be simple (`Save`) or qualified to disambiguate (`OrderService.Save`, `MyApp.Services.OrderService`); `OrderService.Save` also finds `OrderService<T>.Save`.
`solutionPath` accepts a `.sln`, `.slnx` or `.csproj` file, or a directory that contains exactly one of them.

> **Upgrading:** tool names used to be `search_symbols`, `find_references`, etc. They now carry the `roslyn_` prefix. `roslyn_analyze_dependencies` takes `maxNamespaces` instead of `maxDepth` and no longer lists NuGet packages or referenced assemblies (use `dotnet_package List` and `dotnet_project Dependencies`).

## Troubleshooting

- **Lots of `CS0246`/`CS0234` "type or namespace not found" errors.** Packages are not restored. `roslyn_get_diagnostics` puts an `RMCP001` entry at the top of the list when this happens. Run `dotnet_project Restore` (or `dotnet restore`); the workspace reloads by itself once `obj/project.assets.json` changes.
- **"Workspace loaded with N MSBuild warnings".** The first result after a load ends with this note once. Call `roslyn_reload_workspace` to see the warnings; they usually name a missing SDK, workload or project file.
- **"Multiple solution files found" / "Multiple project files found".** `solutionPath` is a directory with more than one `.sln`/`.slnx` (or, without a solution, more than one `.csproj`). Pass the file you want.
- **"Failed to register MSBuild" on startup.** No .NET 10 SDK was found. Install it; a runtime alone is not enough.
- **The `.mcpb` extension fails to start on macOS.** The bundle starts `dotnet` by name, and Claude Desktop, like other apps opened from the Dock or Finder, does not read your shell profile. A `dotnet` that only your shell's PATH finds (for example one installed into `~/.dotnet` by `dotnet-install.sh`) is therefore not found. Look in the MCP logs under `~/Library/Logs/Claude/` for an error such as `spawn dotnet ENOENT`, and run `which dotnet` in Terminal to see where yours lives. Install the .NET 10 SDK with Microsoft's macOS installer package, which puts it in `/usr/local/share/dotnet`, and restart Claude Desktop. If `dotnet` is still not found, register the built server in `claude_desktop_config.json` by absolute path instead (see [Configure](#configure)), adding `"env": { "DOTNET_ROOT": "/path/to/folder/containing/dotnet" }` when the SDK is not in the default location.

## Development

```powershell
dotnet build Roslyn.Mcp.slnx
dotnet test                       # run from the repo root
```

Run both from the repository root: with no arguments, `dotnet test` tests the solution in the current directory. `global.json` selects Microsoft.Testing.Platform as the test runner, which takes a solution path through `--solution`; CI runs `dotnet build Roslyn.Mcp.slnx -c Release` and then `dotnet test --solution Roslyn.Mcp.slnx -c Release --no-build` on Ubuntu and Windows. The end-to-end tests start the built `roslyn-mcp` over stdio, against the sample solution in `tests/fixtures/SampleSolution`.

To try the tools interactively, start the [MCP Inspector](https://github.com/modelcontextprotocol/inspector) on a Debug build:

```powershell
npx @modelcontextprotocol/inspector src/Roslyn.Mcp/bin/Debug/net10.0/roslyn-mcp.exe
```

This repository's `.mcp.json` registers the Debug build for Claude Code. See [AGENTS.md](AGENTS.md) for layout and conventions.

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
