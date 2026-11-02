#Requires -Version 7.0
<#
.SYNOPSIS
    Builds the framework-dependent Roslyn MCP bundle: artifacts/roslyn-mcp-<version>.mcpb.

.DESCRIPTION
    1. Reads <Version> from Directory.Build.props.
    2. Publishes src/Roslyn.Mcp (Release, framework-dependent) into the stage's server/ folder.
    3. Writes manifest.json with ${VERSION} substituted, and LICENSE, to the stage root.
    4. Checks the staged manifest: required fields, version, entry point, and that its tool
       names match the [McpServerTool(Name = ...)] attributes in the source.
    5. Runs `npx @anthropic-ai/mcpb@2.1.2 validate` when npx is available; otherwise skips it.
    6. Zips the stage (without *.pdb) to artifacts/roslyn-mcp-<version>.mcpb, replacing an
       existing bundle of the same version.

    The bundle runs `dotnet server/roslyn-mcp.dll`, so the host needs the .NET 10 SDK.

.EXAMPLE
    pwsh packaging/pack.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $repoRoot 'artifacts'
$stage = Join-Path $artifacts 'mcpb-stage'
$toolsSource = Join-Path $repoRoot 'src' 'Roslyn.Mcp' 'Tools' 'CodeNavigationTools.cs'

function Get-JsonValue([object]$Object, [string]$Path) {
    foreach ($part in $Path.Split('.')) {
        if ($null -eq $Object) { return $null }
        $property = $Object.PSObject.Properties[$part]
        if (-not $property) { return $null }
        $Object = $property.Value
    }
    return $Object
}

function Test-StagedManifest([string]$ManifestPath, [string]$StageRoot, [string]$ExpectedVersion) {
    try {
        $manifest = Get-Content -Raw -LiteralPath $ManifestPath | ConvertFrom-Json
    }
    catch {
        throw "Manifest check failed: $ManifestPath is not valid JSON after substitution. $($_.Exception.Message)"
    }

    $required = 'manifest_version', 'name', 'version', 'description', 'author.name',
                'server.type', 'server.entry_point', 'server.mcp_config.command'
    $missing = $required | Where-Object { [string]::IsNullOrWhiteSpace([string](Get-JsonValue $manifest $_)) }
    if ($missing) {
        throw "Manifest check failed: required field(s) missing or empty: $($missing -join ', ')."
    }

    if ($manifest.version -ne $ExpectedVersion) {
        throw "Manifest check failed: version is '$($manifest.version)' but Directory.Build.props has '$ExpectedVersion'."
    }

    $entryPoint = Join-Path $StageRoot $manifest.server.entry_point
    if (-not (Test-Path -LiteralPath $entryPoint -PathType Leaf)) {
        throw "Manifest check failed: server.entry_point '$($manifest.server.entry_point)' does not exist in the stage ($entryPoint)."
    }

    $sourceTools = @([regex]::Matches((Get-Content -Raw -LiteralPath $toolsSource), '\[McpServerTool\(\s*Name\s*=\s*"([^"]+)"') |
        ForEach-Object { $_.Groups[1].Value } | Sort-Object)
    if ($sourceTools.Count -eq 0) {
        throw "Manifest check failed: no [McpServerTool(Name = ...)] attributes found in $toolsSource."
    }
    $manifestTools = @($manifest.tools | ForEach-Object { $_.name } | Sort-Object)
    $difference = Compare-Object -ReferenceObject $sourceTools -DifferenceObject $manifestTools
    if ($difference) {
        $onlyInSource = @($difference | Where-Object SideIndicator -eq '<=' | ForEach-Object InputObject)
        $onlyInManifest = @($difference | Where-Object SideIndicator -eq '=>' | ForEach-Object InputObject)
        throw ("Manifest check failed: tools do not match the source. " +
               "Missing from manifest: [$($onlyInSource -join ', ')]. Not in source: [$($onlyInManifest -join ', ')].")
    }
    if ($manifestTools.Count -ne ($manifestTools | Select-Object -Unique).Count) {
        throw 'Manifest check failed: the tools list contains a duplicate name.'
    }

    Write-Host "   Manifest OK: version $ExpectedVersion, $($manifestTools.Count) tools match the source."
}

# 1. Version
$props = [xml](Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'Directory.Build.props'))
$version = $props.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
if ([string]::IsNullOrWhiteSpace($version)) {
    throw 'No <Version> found in Directory.Build.props.'
}
$version = $version.Trim()
$bundle = Join-Path $artifacts "roslyn-mcp-$version.mcpb"

Write-Host "=== Roslyn MCP bundle $version ===" -ForegroundColor Cyan

# 2. Publish
if (Test-Path -LiteralPath $stage) {
    Remove-Item -LiteralPath $stage -Recurse -Force
}
New-Item -ItemType Directory -Path $stage | Out-Null

Write-Host '1. Publishing src/Roslyn.Mcp (Release, framework-dependent)...'
& dotnet publish (Join-Path $repoRoot 'src' 'Roslyn.Mcp') -c Release --no-self-contained -o (Join-Path $stage 'server')
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}
# The bundle runs the dll with dotnet, so drop symbols and the build machine's native launcher
# (roslyn-mcp.exe / roslyn-mcp). Removing it here, rather than publishing with UseAppHost=false,
# leaves the normal bin/Release output (used by scripts/setup.ps1) untouched.
Get-ChildItem -LiteralPath $stage -Recurse -Filter '*.pdb' | Remove-Item -Force
Get-ChildItem -LiteralPath (Join-Path $stage 'server') -File |
    Where-Object { $_.Name -in 'roslyn-mcp.exe', 'roslyn-mcp' } | Remove-Item -Force

# 3. Manifest and license
Write-Host '2. Writing manifest.json and LICENSE...'
$manifestTemplate = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'manifest.json')
$stagedManifest = Join-Path $stage 'manifest.json'
[System.IO.File]::WriteAllText($stagedManifest, $manifestTemplate.Replace('${VERSION}', $version), [System.Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination $stage

# 4. Structural check
Write-Host '3. Checking the manifest...'
Test-StagedManifest -ManifestPath $stagedManifest -StageRoot $stage -ExpectedVersion $version

# 5. Schema validation
if (Get-Command npx -ErrorAction SilentlyContinue) {
    Write-Host '4. Validating with @anthropic-ai/mcpb...'
    # Pinned so a new CLI release cannot change what the schema check accepts.
    & npx --yes @anthropic-ai/mcpb@2.1.2 validate $stagedManifest
    if ($LASTEXITCODE -ne 0) {
        throw "mcpb validate failed with exit code $LASTEXITCODE."
    }
}
else {
    Write-Host '4. npx not found: skipping `mcpb validate` (install Node.js to run the schema check).' -ForegroundColor Yellow
}

# 6. Zip
Write-Host '5. Creating the bundle...'
if (Test-Path -LiteralPath $bundle) {
    Remove-Item -LiteralPath $bundle -Force
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $bundle, [System.IO.Compression.CompressionLevel]::Optimal, $false)

Write-Host
Write-Host "Bundle: $bundle" -ForegroundColor Green
