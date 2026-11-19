$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $PSScriptRoot 'PluginRelease.psm1') -Force
$plugins = @(Get-PluginRegistry)

foreach ($plugin in $plugins) {
    $version = Get-PluginVersion $plugin
    foreach ($path in @($plugin.project) + @($plugin.tests)) {
        if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot $path))) {
            throw "$($plugin.key) references missing project '$path'."
        }
    }

    [xml] $pluginProjectXml = Get-Content -LiteralPath (Join-Path $repositoryRoot $plugin.project) -Raw
    $assemblyName = $pluginProjectXml.Project.PropertyGroup.AssemblyName
    $packageId = $pluginProjectXml.Project.PropertyGroup.PackageId
    $rootNamespace = $pluginProjectXml.Project.PropertyGroup.RootNamespace
    $projectName = [IO.Path]::GetFileNameWithoutExtension($plugin.project)
    $componentName = "LANCommander.Community.$($plugin.key)"
    if ($rootNamespace -cne $componentName) {
        throw "$($plugin.key) entry project and root namespace must be '$componentName' (no .Plugin suffix)."
    }
    if ($plugin.assemblyName -cne $projectName -or $assemblyName -cne $projectName -or
        $plugin.packageId -cne $projectName -or $packageId -cne $projectName) {
        throw "$($plugin.key) assembly and package IDs must match its project name '$projectName'."
    }
    if ($rootNamespace -cne $plugin.rootNamespace -or $rootNamespace -notmatch '^LANCommander\.Community\.') {
        throw "$($plugin.key) root namespace does not match the Community naming registry."
    }
    $packageTypes = @(([string]$pluginProjectXml.Project.PropertyGroup.PackageType).Split(';', [StringSplitOptions]::RemoveEmptyEntries))
    $expectedTypes = @($plugin.packageTypes)
    if (@(Compare-Object $expectedTypes $packageTypes).Count -gt 0) {
        throw "$($plugin.key) PackageType metadata does not match the release registry."
    }
    $packageTags = @(([string]$pluginProjectXml.Project.PropertyGroup.PackageTags).Split(';', [StringSplitOptions]::RemoveEmptyEntries))
    foreach ($requiredTag in @($plugin.packageTags)) {
        if ($requiredTag -notin $packageTags) {
            throw "$($plugin.key) is missing required package search tag '$requiredTag'."
        }
    }
    if ($plugin.artifactFormat -eq 'zip' -and $expectedTypes.Count -gt 0) {
        throw "$($plugin.key) ZIP artifacts must not claim NuGet package types."
    }
    if ($plugin.artifactFormat -eq 'zip' -and $pluginProjectXml.Project.PropertyGroup.IsPackable -cne 'false') {
        throw "$($plugin.key) ZIP-only project must remain non-packable as a NuGet package."
    }
    if ($plugin.artifactFormat -eq 'nupkg' -and $expectedTypes.Count -eq 0) {
        throw "$($plugin.key) NuGet artifacts must declare their host package types."
    }
    if ($plugin.githubPackagesEligible -isnot [bool] -or
        $plugin.nugetOrgEligible -isnot [bool] -or
        (($plugin.githubPackagesEligible -or $plugin.nugetOrgEligible) -and $plugin.artifactFormat -cne 'nupkg')) {
        throw "$($plugin.key) package-feed eligibility must be explicit bools and only nupkg artifacts may be eligible."
    }
    if (($plugin.githubPackagesEligible -or $plugin.nugetOrgEligible) -and
        [string]$pluginProjectXml.Project.PropertyGroup.RepositoryUrl -cne 'https://github.com/LANCommander/LANCommander.Community') {
        throw "$($plugin.key) must link package feeds to the canonical LANCommander.Community repository."
    }
}

$allProjects = Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'plugins') -Filter '*.csproj' -File -Recurse
foreach ($project in $allProjects) {
    [xml] $projectXml = Get-Content -LiteralPath $project.FullName -Raw
    $projectName = [IO.Path]::GetFileNameWithoutExtension($project.Name)
    $assemblyName = [string]$projectXml.Project.PropertyGroup.AssemblyName
    $packageId = [string]$projectXml.Project.PropertyGroup.PackageId
    $relative = [IO.Path]::GetRelativePath((Join-Path $repositoryRoot 'plugins'), $project.FullName)
    $componentKey = ($relative -split '[/\\]')[0]
    $component = @($plugins | Where-Object key -CEQ $componentKey)
    $componentRoot = "LANCommander.Community.$componentKey"
    if ($component.Count -ne 1 -or
        ($projectName -cne $componentRoot -and -not $projectName.StartsWith("$componentRoot.", [StringComparison]::Ordinal)) -or
        [string]$projectXml.Project.PropertyGroup.RootNamespace -cne $projectName) {
        throw "Project '$($project.FullName)' must use its registered component root and matching namespace."
    }
    if ($projectName -cne $componentRoot -and $projectXml.Project.PropertyGroup.IsPackable -eq 'true') {
        throw "Helper/test project '$projectName' must not be packable."
    }
    if ($assemblyName -and $assemblyName -cne $projectName) {
        throw "Project '$($project.FullName)' overrides AssemblyName '$assemblyName'; expected '$projectName'."
    }
    if ($packageId -and $packageId -cne $projectName) {
        throw "Project '$($project.FullName)' overrides PackageId '$packageId'; expected '$projectName'."
    }

    $evaluation = & dotnet msbuild $project.FullName -nologo `
        -getProperty:TargetFramework,ImplicitUsings,Nullable,TreatWarningsAsErrors,Version,PackageVersion,IsPackable
    if ($LASTEXITCODE -ne 0) { throw "Could not evaluate build defaults for '$projectName'." }
    $properties = ($evaluation -join "`n" | ConvertFrom-Json).Properties
    $expectedVersion = Get-PluginVersion $component[0]
    if ($properties.TargetFramework -cne 'net10.0' -or
        $properties.ImplicitUsings -cne 'enable' -or $properties.Nullable -cne 'enable' -or
        $properties.TreatWarningsAsErrors -cne 'true' -or
        $properties.Version -cne $expectedVersion -or $properties.PackageVersion -cne $expectedVersion -or
        $properties.IsPackable -cne ($projectName -ceq $componentRoot).ToString().ToLowerInvariant()) {
        throw "Project '$projectName' lost shared defaults, component version, or packaging isolation."
    }
}

$requiredFiles = @(
    'plugins/Server.Mcp/LICENSE',
    'plugins/Server.Mcp/THIRD-PARTY-NOTICES.txt',
    'plugins/Server.Mcp/licenses/MCP.txt',
    'plugins/Server.Mcp/licenses/Microsoft.Extensions.txt',
    'plugins/Server.RecompCatalog/LICENSE',
    'plugins/Launcher.MQTT/examples/MQTTnet-LICENSE.txt'
)
foreach ($path in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot $path))) {
        throw "Missing imported license or third-party notice: $path"
    }
}

$example = Get-Content -LiteralPath (Join-Path $repositoryRoot 'plugins/Launcher.MQTT/src/LANCommander.Community.Launcher.MQTT/homeassistant.example.json') -Raw |
    ConvertFrom-Json
if ($example.BrokerUsername -or $example.BrokerPassword -or $example.UseTls) {
    throw 'The launcher example must contain blank credentials and retain the plaintext default.'
}

$launcherIds = Get-Content -LiteralPath (Join-Path $repositoryRoot 'plugins/Launcher.MQTT/src/LANCommander.Community.Launcher.MQTT/HomeAssistantLauncherPlugin.cs') -Raw
if ($launcherIds -notmatch 'Id = "com\.aaronpowell\.lancommander\.homeassistant"') {
    throw 'The existing launcher plugin ID changed.'
}
$mcpIds = Get-Content -LiteralPath (Join-Path $repositoryRoot 'plugins/Server.Mcp/src/LANCommander.Community.Server.Mcp/McpPlugin.cs') -Raw
if ($mcpIds -notmatch 'io\.github\.aaronpowell\.lancommander\.mcp') {
    throw 'The existing MCP plugin ID changed.'
}
$recompIds = Get-Content -LiteralPath (Join-Path $repositoryRoot 'plugins/Server.RecompCatalog/LANCommander.Community.Server.RecompCatalog/RecompCatalogPlugin.cs') -Raw
if ($recompIds -notmatch 'dev\.aaronpowell\.lancommander\.recompcatalog') {
    throw 'The existing Recomp Catalog plugin ID changed.'
}

$projects = Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'plugins') -Filter '*.csproj' -File -Recurse
foreach ($project in $projects) {
    [xml] $xml = Get-Content -LiteralPath $project.FullName -Raw
    foreach ($reference in $xml.SelectNodes('//PackageReference[@Version]')) {
        throw "Central Package Management is bypassed by $($project.FullName): $($reference.Include)."
    }
}

$trackedConfigs = @(& git -C $repositoryRoot ls-files -- 'plugins/Launcher.MQTT/**/homeassistant.json')
if ($LASTEXITCODE -ne 0) {
    throw 'Could not inspect tracked launcher configuration files.'
}
if ($trackedConfigs.Count -gt 0) {
    throw 'A populated launcher homeassistant.json must not be tracked; only the blank example belongs in source control.'
}

$skills = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot '.github/skills') -Filter SKILL.md -Recurse -File)
foreach ($skill in $skills) {
    $content = Get-Content -LiteralPath $skill.FullName -Raw
    $frontmatter = [regex]::Match($content, '\A---\r?\nname: (?<name>[a-z0-9-]+)\r?\ndescription: (?<description>[^\r\n]+)\r?\n---(?:\r?\n|$)')
    if (-not $frontmatter.Success -or $frontmatter.Groups['name'].Value -cne $skill.Directory.Name -or
        $frontmatter.Groups['description'].Value -notmatch '^Use when ') {
        throw "Skill '$($skill.FullName)' requires matching name and a 'Use when' description in YAML frontmatter."
    }
    foreach ($link in [regex]::Matches($content, '\[[^\]]+\]\((?<target>[^)]+)\)')) {
        $target = $link.Groups['target'].Value
        if ($target -match '^(https?://|#)') { continue }
        $relativePath = ($target -split '#', 2)[0]
        if (-not (Test-Path -LiteralPath (Join-Path $skill.Directory.FullName $relativePath))) {
            throw "Skill '$($skill.Name)' in '$($skill.Directory.Name)' has a broken link: $target"
        }
    }
}
Write-Output "Repository checks passed for $($plugins.Count) components and $($skills.Count) skills."
