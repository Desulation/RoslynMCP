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

## Usage

Once configured, Here are some example queries:

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

## Available Tools

1. **SearchSymbols** - Search for symbols using wildcard patterns
2. **FindReferences** - Find all references to a specific symbol
3. **GetSymbolInfo** - Get detailed information about a symbol
4. **AnalyzeDependencies** - Analyze project dependencies and usage patterns
5. **AnalyzeCodeComplexity** - Identify high-complexity methods

## Development and Testing

### Using MCP Inspector

For development and testing, you can use RoslynMcpServer.http:

1. Update solutionPath in RoslynMcpServer.http to point to your .sln file. You can also modify the search pattern and symbol type as needed.

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.