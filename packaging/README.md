# Packaging

`pack.ps1` builds `artifacts/roslyn-mcp-<version>.mcpb`, an [MCP Bundle](https://github.com/modelcontextprotocol/mcpb): a zip with `manifest.json` at the root and the published server in `server/`. Clients that support `.mcpb` files, such as Claude Desktop, install it in one step.

The bundle is framework-dependent. It runs `dotnet server/roslyn-mcp.dll`, because MSBuildWorkspace loads projects with the installed SDK's MSBuild and cannot be trimmed or published self-contained.

## Prerequisite

The .NET 10 SDK, on the machine that builds the bundle and on every machine that runs it. `dotnet` must be on PATH. A .NET runtime alone is not enough.

## Build

```powershell
pwsh packaging/pack.ps1
```

The script:

1. reads `<Version>` from `Directory.Build.props`, the only place the version is written;
2. publishes `src/Roslyn.Mcp` (Release, framework-dependent) to `artifacts/mcpb-stage/server`, without `.pdb` files;
3. writes `manifest.json` with `${VERSION}` substituted, plus `LICENSE`, to the stage;
4. checks the staged manifest: required fields, the version, the entry point, and that its `tools` names match the `[McpServerTool(Name = ...)]` attributes in `src/Roslyn.Mcp/Tools/CodeNavigationTools.cs`. When you add, rename or remove a tool, update `manifest.json` too;
5. runs `npx @anthropic-ai/mcpb validate` when Node.js is installed, and skips it with a note otherwise;
6. zips the stage to `artifacts/roslyn-mcp-<version>.mcpb`, replacing an existing bundle of the same version.

## Install

- **Claude Desktop:** double-click the `.mcpb` file, or drag it onto Claude Desktop's Extensions settings page, then confirm the install.
- **Other clients:** follow the client's instructions for installing an `.mcpb` file. Clients without `.mcpb` support can run the server from source; see the main [README](../README.md#install).

Install dotnet-mcp alongside it (see the main README's Configure section) so the model can build and test as well as read code.
