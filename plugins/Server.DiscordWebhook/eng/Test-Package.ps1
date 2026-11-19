[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $PackagePath,
    [Parameter(Mandatory)][string] $ExpectedVersion
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
$zip = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($PackagePath))
try {
    $dlls = @($zip.Entries | Where-Object FullName -like '*.dll' | ForEach-Object FullName)
    if ($dlls.Count -ne 1 -or $dlls[0] -cne 'lib/net10.0/LANCommander.Community.Server.DiscordWebhook.dll') {
        throw "Discord package must bundle only its private plugin assembly; found $($dlls -join ', ')."
    }
    $specEntry = @($zip.Entries | Where-Object FullName -like '*.nuspec')
    if ($specEntry.Count -ne 1) { throw 'Expected one Discord nuspec.' }
    $reader = [IO.StreamReader]::new($specEntry[0].Open())
    try { [xml] $spec = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $ns = [Xml.XmlNamespaceManager]::new($spec.NameTable)
    $ns.AddNamespace('n', $spec.DocumentElement.NamespaceURI)
    if ($spec.SelectNodes('//n:dependency', $ns).Count -ne 0) {
        throw 'Host dependencies must not be declared as private runtime dependencies.'
    }
    $metadata = $spec.SelectSingleNode('/n:package/n:metadata', $ns)
    if ($metadata.id -cne 'LANCommander.Community.Server.DiscordWebhook' -or
        $metadata.version -cne $ExpectedVersion -or
        $metadata.repository.url -cne 'https://github.com/LANCommander/LANCommander.Community') {
        throw 'Discord package identity, version or canonical repository mismatch.'
    }
    if (-not $zip.GetEntry('README.md')) { throw 'Discord package README is missing.' }
}
finally { $zip.Dispose() }

$previous = $env:DISCORD_TEST_PACKAGE
try {
    $env:DISCORD_TEST_PACKAGE = [IO.Path]::GetFullPath($PackagePath)
    & dotnet test (Join-Path $PSScriptRoot '../tests/LANCommander.Community.Server.DiscordWebhook.Tests/LANCommander.Community.Server.DiscordWebhook.Tests.csproj') `
        -c Release --no-build --no-restore --nologo --filter 'FullyQualifiedName~PackagedPluginLoads'
    if ($LASTEXITCODE -ne 0) { throw 'Packaged Discord host-contract loader check failed.' }
}
finally { $env:DISCORD_TEST_PACKAGE = $previous }
Write-Output "Discord private-only package and loader verified: $ExpectedVersion"
