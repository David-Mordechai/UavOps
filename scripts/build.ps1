<#
.SYNOPSIS
    Builds UavOps.Agent and all 3 MCP server projects, lays each MCP server out in its own folder
    beside UavOps.Agent's own appsettings.json, and points McpServerPaths at them (relative paths).
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutputDir,
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDir) {
    $OutputDir = Join-Path $repoRoot 'publish'
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "dotnet CLI not found on PATH. Install the .NET 8 SDK first."
}

function Invoke-DotnetPublish([string]$ProjectPath, [string]$OutDir, [string]$Label) {
    Write-Host "Publishing $Label -> $OutDir"
    dotnet publish $ProjectPath -c $Configuration -o $OutDir
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $Label (exit code $LASTEXITCODE)."
    }
}

if ($Clean -and (Test-Path $OutputDir)) {
    Write-Host "Cleaning $OutputDir"
    Remove-Item $OutputDir -Recurse -Force
}

$mcpServers = [ordered]@{
    moav      = 'UavOps.Agent.McpMoav'
    watchdog  = 'UavOps.Agent.McpWatchdog'
    simulator = 'UavOps.Agent.McpSimulator'
}

$agentOut = Join-Path $OutputDir 'UavOps.Agent'
Invoke-DotnetPublish (Join-Path $repoRoot 'src\UavOps.Agent\UavOps.Agent.csproj') $agentOut 'UavOps.Agent'

$relativePaths = [ordered]@{}
foreach ($key in $mcpServers.Keys) {
    $projectName = $mcpServers[$key]
    $mcpOut = Join-Path $agentOut "mcp-$key"
    Invoke-DotnetPublish (Join-Path $repoRoot "src\$projectName\$projectName.csproj") $mcpOut $projectName
    $relativePaths[$key] = "mcp-$key/$projectName.dll"
}

$appsettingsPath = Join-Path $agentOut 'appsettings.json'
$json = Get-Content $appsettingsPath -Raw | ConvertFrom-Json
foreach ($key in $relativePaths.Keys) {
    $json.McpServerPaths.$key = $relativePaths[$key]
}
$json | ConvertTo-Json -Depth 10 | Set-Content $appsettingsPath

Write-Host ""
Write-Host "Build complete. Output: $agentOut"
foreach ($key in $relativePaths.Keys) {
    Write-Host "  McpServerPaths.$key = $($relativePaths[$key])"
}
