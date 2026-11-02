#Requires -Version 7.0
<#
.SYNOPSIS
    Builds Roslyn MCP and registers it, next to dotnet-mcp, in an MCP client config file.

.DESCRIPTION
    1. Builds Roslyn.Mcp.slnx (unless -SkipBuild).
    2. Checks that roslyn-mcp --version prints a version.
    3. Merges two entries into the config's "mcpServers" object:
         dotnet  -> dnx Community.Mcp.DotNet@1.* --yes
         roslyn  -> the built roslyn-mcp executable
       Every other server in the file is kept. The file and its directory are created if missing.

.PARAMETER ConfigPath
    The client config file to update. Defaults to Claude Desktop's
    %APPDATA%\Claude\claude_desktop_config.json.

.PARAMETER Configuration
    The build configuration whose executable is registered. Defaults to Release.

.PARAMETER SkipBuild
    Register an existing build instead of building first.

.EXAMPLE
    pwsh scripts/setup.ps1

.EXAMPLE
    pwsh scripts/setup.ps1 -ConfigPath C:\path\to\mcp.json -Configuration Debug -SkipBuild
#>
[CmdletBinding()]
param(
    [string]$ConfigPath,
    [string]$Configuration = 'Release',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot 'Roslyn.Mcp.slnx'
$exeName = if ($IsWindows) { 'roslyn-mcp.exe' } else { 'roslyn-mcp' }
$exe = Join-Path $repoRoot 'src' 'Roslyn.Mcp' 'bin' $Configuration 'net10.0' $exeName

if (-not $ConfigPath) {
    if (-not $env:APPDATA) {
        throw 'APPDATA is not set, so the Claude Desktop config path is unknown. Pass -ConfigPath.'
    }
    $ConfigPath = Join-Path $env:APPDATA 'Claude' 'claude_desktop_config.json'
}
$ConfigPath = [System.IO.Path]::GetFullPath($ConfigPath)

Write-Host '=== Roslyn MCP setup ===' -ForegroundColor Cyan
Write-Host "Repository: $repoRoot"
Write-Host "Config:     $ConfigPath"
Write-Host

# 1. Build
if ($SkipBuild) {
    Write-Host '1. Build skipped (-SkipBuild).' -ForegroundColor Yellow
}
else {
    Write-Host "1. Building $solution ($Configuration)..." -ForegroundColor Yellow
    dotnet build $solution -c $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE."
    }
}

# 2. Verify the executable
Write-Host '2. Checking roslyn-mcp --version...' -ForegroundColor Yellow
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "The server executable was not found at '$exe'. Build first, or check -Configuration."
}
$version = (& $exe --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $version -notmatch '^\d+\.\d+\.\d+') {
    throw "'$exe --version' did not print a version (exit code $LASTEXITCODE, output '$version')."
}
Write-Host "   roslyn-mcp $version"

# 3. Merge the config
Write-Host '3. Updating the config...' -ForegroundColor Yellow
$config = $null
if (Test-Path -LiteralPath $ConfigPath -PathType Leaf) {
    $json = Get-Content -LiteralPath $ConfigPath -Raw
    if (-not [string]::IsNullOrWhiteSpace($json)) {
        try {
            $config = $json | ConvertFrom-Json -AsHashtable
        }
        catch {
            throw "Could not parse '$ConfigPath' as JSON, so it was left unchanged: $($_.Exception.Message)"
        }
        if ($config -isnot [System.Collections.IDictionary]) {
            throw "'$ConfigPath' does not contain a JSON object, so it was left unchanged."
        }
    }
}
else {
    $directory = Split-Path -Parent $ConfigPath
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
}

if ($null -eq $config) {
    $config = [ordered]@{}
}
if (-not $config.Contains('mcpServers') -or $null -eq $config['mcpServers']) {
    $config['mcpServers'] = [ordered]@{}
}
elseif ($config['mcpServers'] -isnot [System.Collections.IDictionary]) {
    throw "'mcpServers' in '$ConfigPath' is not a JSON object, so the file was left unchanged."
}

$servers = $config['mcpServers']
$servers['dotnet'] = [ordered]@{ command = 'dnx'; args = @('Community.Mcp.DotNet@1.*', '--yes') }
$servers['roslyn'] = [ordered]@{ command = $exe }

$config | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $ConfigPath -Encoding utf8NoBOM

$others = @($servers.Keys | Where-Object { $_ -notin 'dotnet', 'roslyn' })
Write-Host '   Wrote the dotnet and roslyn entries.'
if ($others.Count -gt 0) {
    Write-Host "   Kept the other servers: $($others -join ', ')"
}

# 4. Next steps
Write-Host
Write-Host '=== Done ===' -ForegroundColor Cyan
Write-Host 'Next steps:'
Write-Host '  1. Restart your MCP client (for Claude Desktop, quit it from the tray and start it again).'
Write-Host '  2. Check that the dotnet_* and roslyn_* tools are listed.'
Write-Host '  3. Try: "Search for all classes ending with Service in C:\path\to\YourSolution.sln"'
Write-Host
Write-Host 'The dotnet entry runs dnx, which ships with the .NET 10 SDK.'
Write-Host "To try the server without a client: npx @modelcontextprotocol/inspector `"$exe`""
