# Roslyn MCP Server

A C# MCP (Model Context Protocol) server that integrates with Microsoft's Roslyn compiler platform to provide Claude Desktop, LM Studio, VS Code or Visual Studio with code analysis and navigation capabilities for C# codebases.

It is built to complement the [.NET MCP Server (dotnet-mcp)](https://github.com/jongalloway/dotnet-mcp): dotnet-mcp drives the .NET SDK (create, add packages, build, test, run) and this server gives the model a read-only, semantic view of the code. See [Using with the .NET MCP Server](#using-with-the-net-mcp-server-dotnet-mcp).

## Features

- **Wildcard Symbol Search** - Find classes, methods, and properties using pattern matching (`*Service`, `Get*User`, etc.)
- **Reference Tracking** - Locate all usages of symbols across entire solutions
- **Implementations and Callers** - Find interface implementations, derived classes, overrides and call sites
- **File Outlines** - List a file's types and members with line ranges so only the relevant lines need reading
- **Compiler Diagnostics** - Errors and warnings from the live workspace without running a build
- **Stays in Sync** - Picks up edits, new files, package and project changes made by you or by dotnet-mcp
- **Symbol Information** - Get detailed information about types, methods, properties, and more
- **Dependency Analysis** - Analyze project dependencies and namespace usage patterns
- **Code Complexity Analysis** - Identify high-complexity methods using cyclomatic complexity metrics
- **Performance Optimized** - Cached workspace with incremental updates for edited files
- **Security** - Input validation and path sanitization

## Prerequisites

- .NET 10.0 SDK or later
- Visual Studio 2026
- LM Studio

## Installation

1. **Clone or download the project**
   ```bash
   cd D:\Source\RoslynMCP\RoslynMcpServer
   ```

2. **Restore NuGet packages**
   ```bash
   dotnet restore
   ```

3. **Build the project**
   ```bash
   dotnet build
   ```

4. **Test the server** (optional)
   ```bash
   dotnet run
   ```

## Setup LM Studio
1. Open up the right sidebar clocking the icon in the top right.
2. Under the "Integrations" tab click Install to edit your mcp.json
3. Edit your mcp.json to look something like this.

* Using URL (start the server first with `dotnet run` in `RoslynMcpServer`)
```json
{
  "mcpServers": {
    "RoslynExplorer": {
      "url": "http://localhost:16488"
    }
  }
}
```

* Or let LM Studio start it over stdio, next to dotnet-mcp. See [Configuring both servers](#configuring-both-servers).

## Using with the .NET MCP Server (dotnet-mcp)

This server is designed to sit alongside [jongalloway/dotnet-mcp](https://github.com/jongalloway/dotnet-mcp).
The two servers cover different sides of .NET work and do not overlap:

| | dotnet-mcp (`dotnet_*`) | Roslyn MCP (`roslyn_*`) |
|---|---|---|
| Focus | The .NET SDK and CLI | The C# compiler's semantic model |
| Changes things | Yes: new projects, packages, references, EF migrations, format | No, read-only |
| Typical tools | `dotnet_project` (New/Build/Test/Run), `dotnet_package`, `dotnet_solution`, `dotnet_sdk` | `roslyn_search_symbols`, `roslyn_find_references`, `roslyn_find_callers`, `roslyn_get_diagnostics` |

How they fit together:

- **Tool names are namespaced.** dotnet-mcp prefixes its tools with `dotnet_`; this server uses `roslyn_`, so they never collide and the model can tell them apart.
- **Server instructions.** On connect, this server tells the model to use `dotnet_*` tools to create, build, test and run, and `roslyn_*` tools to understand code. It suggests a loop: locate code with `roslyn_*`, check what a change affects with `roslyn_find_references`/`roslyn_find_callers`, edit, get fast feedback with `roslyn_get_diagnostics`, then confirm with `dotnet_project` Build/Test.
- **Same paths.** `solutionPath` takes the same solution, project or directory you pass to `dotnet_*` tools.
- **Stays in sync with dotnet-mcp's changes.** The Roslyn workspace is cached, and every 2 seconds at most a request checks it against the file system:
  - Edited `.cs` files are updated in place, which is cheap.
  - Added or removed `.cs` files, a changed `.sln`/`.slnx`/`.csproj`, `Directory.Build.*`, `Directory.Packages.props` or `global.json`, or a restore (`obj/project.assets.json`) trigger a full reload. That covers `dotnet_package Add`, `dotnet_solution Add`, `dotnet_project New` and `dotnet format`.
  - `roslyn_reload_workspace` forces a reload.
- **Same transport.** dotnet-mcp runs over stdio. Start this server with `--stdio` to configure both the same way (HTTP is still the default).

### Configuring both servers

Claude Desktop (`%APPDATA%\Claude\claude_desktop_config.json` on Windows, `~/Library/Application Support/Claude/claude_desktop_config.json` on macOS), VS Code, Visual Studio and LM Studio all accept this shape:

```json
{
  "mcpServers": {
    "dotnet": {
      "command": "dnx",
      "args": ["Community.Mcp.DotNet@1.*", "--yes"]
    },
    "roslyn": {
      "command": "dotnet",
      "args": [
        "run", "--project", "C:\\path\\to\\RoslynMCP\\RoslynMcpServer",
        "--no-launch-profile", "--", "--stdio"
      ]
    }
  }
}
```

`--no-launch-profile` stops `dotnet run` from printing launch-profile messages to stdout, which would corrupt the stdio protocol. After a build you can also point `command` at `RoslynMcpServer\bin\Debug\net10.0\RoslynMcpServer.exe` with `"args": ["--stdio"]`, which starts faster.

To use HTTP instead, run `dotnet run` in `RoslynMcpServer` and register `"roslyn": { "url": "http://localhost:16488" }` next to the `dotnet` entry.

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

## Usage

Once configured, here are some example queries:

### Search for Symbols
```
Search for all classes ending with 'Service' in my solution at C:\MyProject\MyProject.sln
```

### Find References
```
Find all references to the UserRepository class in C:\MyProject\MyProject.sln
```

### Get Symbol Information
```
Get information about the CalculateTotal method in C:\MyProject\MyProject.sln
```

### Analyze Dependencies
```
Analyze dependencies for the solution at C:\MyProject\MyProject.sln
```

### Code Complexity Analysis
```
Find methods with complexity higher than 7 in C:\MyProject\MyProject.sln
```

### Find Implementations and Callers
```
Show me every class that implements IOrderRepository and everything that calls IOrderRepository.Save in C:\MyProject
```

### Outline a File and Check It Compiles
```
Outline C:\MyProject\Api\OrderService.cs and check it for compiler errors
```

## Available Tools

| Tool | What it does |
|------|--------------|
| `roslyn_search_symbols` | Wildcard search (`*Service`, `Get*User`) over symbols declared in source |
| `roslyn_find_references` | Every reference to a symbol, with the line text |
| `roslyn_get_symbol_info` | Kind, signature, accessibility, attributes and declaration location |
| `roslyn_find_implementations` | Implementations of an interface/member, derived classes, overrides |
| `roslyn_find_callers` | Methods/properties that call a method (one level of the call hierarchy) |
| `roslyn_get_document_outline` | Types and members in a `.cs` file with signatures and line ranges |
| `roslyn_get_diagnostics` | Compiler errors/warnings from the in-memory workspace, no build needed |
| `roslyn_analyze_dependencies` | Symbol counts, project/assembly references, namespace usage |
| `roslyn_analyze_code_complexity` | Methods at or above a cyclomatic complexity threshold |
| `roslyn_reload_workspace` | Drop the cached workspace and reload the solution |

All tools are read-only and annotated as such (`readOnlyHint`), so clients can auto-approve them.

Symbol names can be simple (`Save`) or qualified to disambiguate (`OrderService.Save`, `MyApp.Services.OrderService`).
`solutionPath` accepts a `.sln`, `.slnx` or `.csproj` file, or a directory that contains exactly one of them.

> **Upgrading:** tool names used to be `search_symbols`, `find_references`, etc. They now carry the `roslyn_` prefix. Update any allowlists or saved requests.

## Development and Testing

### Using MCP Inspector

For development and testing, you can use RoslynMcpServer.http:

1. Update solutionPath in RoslynMcpServer.http to point to your .sln file. You can also modify the search pattern and symbol type as needed.

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.