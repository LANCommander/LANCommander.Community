$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'PluginRelease.psm1') -Force

function Assert-Selection {
    param([string[]] $Paths, [string[]] $Expected, [string] $Name)
    $actual = @(Get-ImpactedPlugins -ChangedPaths $Paths)
    if (($actual | ConvertTo-Json -Compress) -cne ($Expected | ConvertTo-Json -Compress)) {
        throw "$Name failed: expected '$Expected', got '$actual'."
    }
}

$all = @(Get-PluginRegistry | ForEach-Object key)
Assert-Selection @() @() 'Empty change set'
Assert-Selection @('README.md', 'CONTRIBUTING.md', '.github/skills/plugin-maintenance/SKILL.md') @() 'Non-packaged docs'
Assert-Selection @('plugins/Server.Mcp/README.md', 'plugins/Server.Mcp/src/removed.cs') @('Server.Mcp') 'Component docs and deleted code'
Assert-Selection @('plugins\Launcher.MQTT\src\changed.cs') @('Launcher.MQTT') 'Windows path'
Assert-Selection @('plugins/Server.Mcp/version.txt', 'plugins/Server.Quake3/CHANGELOG.md') @('Server.Quake3', 'Server.Mcp') 'Multiple components in registry order'
foreach ($path in @('Directory.Build.props', 'Directory.Packages.props', 'global.json', 'NuGet.Config',
    'plugins/Directory.Build.props', 'eng/plugins.json', 'eng/Validate-Plugin.ps1',
    'eng/lancommander-source.json', '.github/workflows/ci.yml', 'new-shared-file', 'plugins/Server.New/new.cs')) {
    Assert-Selection @('plugins/Server.Mcp/src/example.cs', $path) $all "Shared or unknown path $path"
}

$script = Join-Path $PSScriptRoot 'Resolve-CISelection.ps1'
$allJson = & $script
if ($allJson -cne (ConvertTo-Json -InputObject $all -Compress)) { throw 'Default selection must produce a JSON array of all plugins.' }
try {
    & $script -BaseCommit 'main' -HeadCommit ('a' * 40)
    throw 'Invalid commit input was accepted.'
}
catch {
    if ($_.Exception.Message -notmatch 'exact base and head commit SHAs') { throw }
}
$emptyJson = ConvertTo-Json -InputObject @(Get-ImpactedPlugins -ChangedPaths @('README.md')) -Compress
$singleJson = ConvertTo-Json -InputObject @(Get-ImpactedPlugins -ChangedPaths @('plugins/Server.Mcp/README.md')) -Compress
if ($emptyJson -cne '[]' -or $singleJson -cne '["Server.Mcp"]') {
    throw 'Empty and single-component matrices must remain JSON arrays.'
}

$fixture = Join-Path ([IO.Path]::GetTempPath()) "ci-selection-$([Guid]::NewGuid().ToString('N'))"
function Invoke-FixtureGit {
    & git -C $fixture @args
    if ($LASTEXITCODE -ne 0) { throw "Git fixture command failed: $args" }
}
New-Item -ItemType Directory -Path (Join-Path $fixture 'eng') | Out-Null
try {
    foreach ($file in @('Resolve-CISelection.ps1', 'PluginRelease.psm1', 'plugins.json')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination (Join-Path $fixture "eng/$file")
    }
    New-Item -ItemType Directory -Path (Join-Path $fixture 'plugins/Server.Mcp'), (Join-Path $fixture 'plugins/Server.Quake3') | Out-Null
    Set-Content (Join-Path $fixture 'plugins/Server.Mcp/example.cs') 'original'
    Invoke-FixtureGit init --quiet
    Invoke-FixtureGit add .
    Invoke-FixtureGit -c user.name=CI -c user.email=ci@example.invalid commit --quiet -m initial
    $base = (Invoke-FixtureGit rev-parse HEAD).Trim()
    $fixtureScript = Join-Path $fixture 'eng/Resolve-CISelection.ps1'
    if ((& $fixtureScript -BaseCommit $base -HeadCommit $base) -cne '[]') { throw 'Empty git diff must produce an empty matrix.' }
    Move-Item -LiteralPath (Join-Path $fixture 'plugins/Server.Mcp/example.cs') -Destination (Join-Path $fixture 'plugins/Server.Quake3/example.cs')
    Invoke-FixtureGit add .
    Invoke-FixtureGit -c user.name=CI -c user.email=ci@example.invalid commit --quiet -m rename
    $head = (Invoke-FixtureGit rev-parse HEAD).Trim()
    if ((& $fixtureScript -BaseCommit $base -HeadCommit $head) -cne '["Server.Quake3","Server.Mcp"]') {
        throw 'Cross-component renames must select both the removed and added package owners.'
    }
    Set-Content (Join-Path $fixture 'Directory.Build.props') '<Project />'
    Invoke-FixtureGit add .
    Invoke-FixtureGit -c user.name=CI -c user.email=ci@example.invalid commit --quiet -m shared
    $head = (Invoke-FixtureGit rev-parse HEAD).Trim()
    if ((& $fixtureScript -BaseCommit $base -HeadCommit $head) -cne $allJson) {
        throw 'Real shared-file diffs must select every component.'
    }
    $previousExitCode = $global:LASTEXITCODE
    try {
        & $fixtureScript -BaseCommit ('0' * 40) -HeadCommit $head 2>$null
        throw 'A missing git commit was accepted.'
    }
    catch {
        if ($_.Exception.Message -notmatch 'Could not determine PR changes') { throw }
    }
    finally {
        $global:LASTEXITCODE = $previousExitCode
    }
}
finally {
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
Write-Output 'CI impact selection tests passed.'
