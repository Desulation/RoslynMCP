# Roslyn MCP Server

A C# MCP (Model Context Protocol) server that integrates with Microsoft's Roslyn compiler platform to provide Claude Desktop or LM Studio with code analysis and navigation capabilities for C# codebases.

## Features

- **Wildcard Symbol Search** - Find classes, methods, and properties using pattern matching (`*Service`, `Get*User`, etc.)
- **Reference Tracking** - Locate all usages of symbols across entire solutions
- **Symbol Information** - Get detailed information about types, methods, properties, and more
- **Dependency Analysis** - Analyze project dependencies and namespace usage patterns
- **Code Complexity Analysis** - Identify high-complexity methods using cyclomatic complexity metrics
- **Performance Optimized** - Multi-level caching and incremental analysis for large codebases
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

* Using URL
```json
{
  "mcpServers": {
    "RoslynExplorer": {
      "url": "http://localhost:16488"      
    }
  }
}
```


## Claude Desktop Configuration

Edit `%APPDATA%\Claude\claude_desktop_config.json` (Windows) or `~/Library/Application Support/Claude/claude_desktop_config.json` (macOS). Claude Desktop launches servers over stdio, so pass `--stdio`:

```json
{
  "mcpServers": {
    "roslyn": {
      "command": "dotnet",
      "args": [
        "run", "--project", "D:\\Source\\RoslynMCP\\RoslynMcpServer",
        "--no-launch-profile", "--", "--stdio"
      ]
    }
  }
}
```

**Important Notes:**
- Replace `D:\\Source\\RoslynMCP\\RoslynMcpServer` with the absolute path to your project
- Use double backslashes (`\\`) in Windows paths for proper JSON escaping
- `--no-launch-profile` keeps `dotnet run` from writing to stdout, which carries the MCP protocol
- Or use the built executable: `"command": "D:\\Source\\RoslynMCP\\RoslynMcpServer\\bin\\Debug\\net10.0\\RoslynMcpServer.exe", "args": ["--stdio"]`

To run it next to the [.NET MCP Server (dotnet-mcp)](https://github.com/jongalloway/dotnet-mcp), see "Using with the .NET MCP Server" in the [repository README](../README.md#using-with-the-net-mcp-server-dotnet-mcp).

## Usage

Once configured, restart Claude Desktop. You should see the Roslyn MCP Server appear in the available tools. Here are the available commands:

### 1. Search Symbols
Search for symbols using wildcard patterns:

**Example:**
```
Search for all classes ending with 'Service' in my solution at C:\MyProject\MyProject.sln
```

This will use the `roslyn_search_symbols` tool with pattern `*Service` and symbol types `class`.

### 2. Find References
Find all references to a specific symbol:

**Example:**
```
Find all references to the UserRepository class in C:\MyProject\MyProject.sln
```

### 3. Get Symbol Information
Get detailed information about a specific symbol:

**Example:**
```
Get information about the CalculateTotal method in C:\MyProject\MyProject.sln
```

### 4. Analyze Dependencies
Analyze project dependencies and usage patterns:

**Example:**
```
Analyze dependencies for the solution at C:\MyProject\MyProject.sln
```

### 5. Analyze Code Complexity
Identify methods with high cyclomatic complexity:

**Example:**
```
Find methods with complexity higher than 7 in C:\MyProject\MyProject.sln
```

## Tool Parameters

Every tool takes `solutionPath`: an absolute path to a `.sln`, `.slnx` or `.csproj` file, or a directory containing exactly one of them.

### roslyn_search_symbols
- `pattern`: Wildcard pattern (* and ? supported)
- `symbolTypes`: Comma-separated list (class,interface,struct,enum,method,property,field,event)
- `ignoreCase`: Boolean (default: true)

### roslyn_find_references
- `symbolName`: Symbol name, optionally qualified (`OrderService.Save`)
- `includeDefinition`: Include symbol definition (default: true)

### roslyn_get_symbol_info
- `symbolName`: Symbol name, optionally qualified

### roslyn_find_implementations
- `symbolName`: Interface, class, or virtual/abstract/interface member

### roslyn_find_callers
- `symbolName`: Method, property or event

### roslyn_get_document_outline
- `filePath`: Absolute path to a `.cs` file in the solution

### roslyn_get_diagnostics
- `filePath`: Optional `.cs` file to check (much faster than the whole solution)
- `minimumSeverity`: Error, Warning or Info (default: Warning)
- `maxResults`: Default 100

### roslyn_analyze_dependencies
- `maxDepth`: Maximum analysis depth (default: 3)

### roslyn_analyze_code_complexity
- `threshold`: Complexity threshold 1-10 (default: 5)

### roslyn_reload_workspace
- No extra parameters. The workspace already refreshes itself when files change; use this to force a reload or see MSBuild load warnings.

## Development and Testing

### Using MCP Inspector

For development and testing, you can use the MCP Inspector:

```bash
# Install the inspector
npm install -g @modelcontextprotocol/inspector

# Test your server
npx @modelcontextprotocol/inspector dotnet run --project D:\Source\RoslynMCP\RoslynMcpServer
```

### Debugging

The server includes comprehensive logging. Check the console output for detailed information about operations and any errors.

### Performance Considerations

- **Large Solutions**: The server uses caching and incremental analysis for better performance
- **Memory Usage**: Large codebases may require significant memory
- **First Run**: Initial analysis may take longer as caches are populated

## Security

The server includes several security measures:
- Path validation to prevent directory traversal
- Input sanitization for search patterns
- File extension validation
- Error handling and safe error messages

## Troubleshooting

### Common Issues

1. **MSBuild not found**
   - Ensure .NET SDK is properly installed
   - Visual Studio Build Tools may be required

2. **Solution file not found**
   - Check that the path is absolute and correct
   - Ensure the .sln file exists and is accessible

3. **Server not appearing in Claude Desktop**
   - Check JSON syntax in configuration file
   - Verify the path to the project is correct
   - Restart Claude Desktop after configuration changes

4. **Performance issues**
   - Large solutions may require more memory
   - Consider increasing timeout values for complex operations

### Logs

Check the console output for detailed error messages and debugging information. The server logs all major operations and errors.

## Architecture

The server is built with a modular architecture:

- **MCP Server Layer**: Handles communication with Claude Desktop
- **Roslyn Integration Layer**: Manages workspaces and compilations
- **Search Engine Layer**: Implements symbol search and analysis
- **Caching Layer**: Multi-level caching for performance optimization
- **Security Layer**: Input validation and sanitization

## Contributing

This is a complete implementation based on the comprehensive guide. Feel free to extend it with additional features like:

- Support for VB.NET projects
- Additional complexity metrics
- Code quality analysis
- Refactoring suggestions
- Integration with other analysis tools