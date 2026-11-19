[CmdletBinding()]
param(
    [string] $BaseCommit,
    [string] $HeadCommit
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'PluginRelease.psm1') -Force
if ($BaseCommit -or $HeadCommit) {
    if ($BaseCommit -cnotmatch '^[0-9a-f]{40}$' -or $HeadCommit -cnotmatch '^[0-9a-f]{40}$') {
        throw 'PR selection requires both exact base and head commit SHAs.'
    }
    $paths = @(& git -C (Split-Path $PSScriptRoot -Parent) diff --name-only --no-renames "$BaseCommit...$HeadCommit" --)
    if ($LASTEXITCODE -ne 0) { throw 'Could not determine PR changes; refusing an incomplete package selection.' }
    $plugins = @(Get-ImpactedPlugins -ChangedPaths $paths)
}
else {
    $plugins = @(Get-PluginRegistry | ForEach-Object key)
}
ConvertTo-Json -InputObject $plugins -Compress
