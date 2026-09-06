#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$SourceRoot = $PSScriptRoot,
    [string]$OutputRoot,
    [switch]$BuildOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'This source preview targets Windows 11 x64; non-Windows execution is not qualified.' }
if (-not [Environment]::Is64BitProcess) { throw 'Use a 64-bit PowerShell 7 process.' }

$previewSource = [IO.Path]::GetFullPath($SourceRoot)
$manifestPath = Join-Path $PSScriptRoot 'SOURCE_PREVIEW_BUILD.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or $manifest.releaseKind -ne 'SOURCE_PREVIEW' -or
    $manifest.displayProcessingIncluded -or $manifest.runtimePayloadIncluded -or $manifest.privateAdapterIncluded) {
    throw 'Unexpected source-preview build manifest.'
}
$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
Push-Location -LiteralPath $previewSource
try {
    $sdk = (& $dotnet --version | Select-Object -Last 1).Trim()
    if ($LASTEXITCODE -ne 0 -or $sdk -notmatch '^10\.') {
        throw 'A .NET 10 SDK is required. Install it separately; this script never installs SDKs or drivers.'
    }
} finally { Pop-Location }

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path ([IO.Path]::GetTempPath()) ('NeraSourcePreview-' + [Guid]::NewGuid().ToString('N'))
}
$previewOutput = [IO.Path]::GetFullPath($OutputRoot)
if ($previewOutput.Equals($previewSource, [StringComparison]::OrdinalIgnoreCase) -or
    $previewOutput.StartsWith($previewSource.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputRoot must be outside the source tree, so obj/bin and test output cannot enter a source release.'
}
if (Test-Path -LiteralPath $previewOutput) {
    throw 'OutputRoot already exists. Choose a new output directory; this script never overwrites or deletes old output.'
}

$projectSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($project in $manifest.projects) {
    $relative = [string]$project.path
    if ([IO.Path]::IsPathRooted($relative) -or $relative -match '(^|[\\/])\.\.([\\/]|$)') {
        throw 'Manifest project paths must stay inside the source root.'
    }
    $absolute = [IO.Path]::GetFullPath((Join-Path $previewSource $relative))
    if (-not (Test-Path -LiteralPath $absolute -PathType Leaf)) { throw "Missing source project: $relative" }
    if (-not $projectSet.Add($absolute)) { throw "Duplicate source project: $relative" }
}

# Verify the declared managed build closure before MSBuild runs. This guards the
# intended preview, not arbitrary untrusted MSBuild projects or a security sandbox.
foreach ($project in $manifest.projects) {
    $absolute = [IO.Path]::GetFullPath((Join-Path $previewSource $project.path))
    [xml]$projectXml = Get-Content -LiteralPath $absolute -Raw
    if ([string]$projectXml.Project.Sdk -ne 'Microsoft.NET.Sdk') { throw "Unexpected SDK in $($project.path)" }
    if ($projectXml.SelectNodes('//PackageReference|//Import|//Target|//Reference|//COMReference').Count -ne 0) {
        throw "Unreviewed dependency or build target in $($project.path)"
    }
    foreach ($reference in $projectXml.SelectNodes('//ProjectReference')) {
        $referenced = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $absolute) ([string]$reference.Include)))
        if (-not $projectSet.Contains($referenced)) { throw "Project reference leaves the reviewed preview closure: $($project.path)" }
    }
    foreach ($linkedFile in $projectXml.SelectNodes('//Compile[@Include]|//EmbeddedResource[@Include]|//Content[@Include]')) {
        throw "Explicit linked content needs review before preview build: $($project.path)"
    }
}

New-Item -ItemType Directory -Path $previewOutput | Out-Null
$buildSource = Join-Path $previewOutput 'source'
New-Item -ItemType Directory -Path $buildSource | Out-Null
$copiedSources = [Collections.Generic.List[object]]::new()
foreach ($buildConfig in @('global.json', 'NuGet.Config')) {
    $configSource = Join-Path $previewSource $buildConfig
    if (-not (Test-Path -LiteralPath $configSource -PathType Leaf)) { throw "Missing reviewed build configuration: $buildConfig" }
    if ((Get-Item -LiteralPath $configSource).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked build configuration rejected.' }
    Copy-Item -LiteralPath $configSource -Destination (Join-Path $buildSource $buildConfig)
    $copiedSources.Add([pscustomobject]@{ path = $buildConfig; bytes = (Get-Item -LiteralPath $configSource).Length; sha256 = (Get-FileHash -LiteralPath $configSource).Hash })
}

function Copy-PreviewSourceDirectory([string]$Directory) {
    foreach ($entry in Get-ChildItem -LiteralPath $Directory -Force) {
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Reparse-point source requires separate review; it will not be followed.'
        }
        if ($entry.PSIsContainer) {
            if ($entry.Name -notin $manifest.excludedDirectoryNames) { Copy-PreviewSourceDirectory $entry.FullName }
            continue
        }
        $relative = [IO.Path]::GetRelativePath($previewSource, $entry.FullName).Replace('\', '/')
        if ($entry.Extension -notin $manifest.sourceFileExtensions -or $relative -in $manifest.excludedFiles) { continue }
        $destination = Join-Path $buildSource $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $entry.FullName -Destination $destination
        $copiedSources.Add([pscustomobject]@{
            path = $relative; bytes = $entry.Length
            sha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
        })
    }
}
foreach ($sourceDirectory in $manifest.sourceDirectories) {
    if ([IO.Path]::IsPathRooted($sourceDirectory) -or $sourceDirectory -match '(^|[\\/])\.\.([\\/]|$)') {
        throw 'Manifest source directories must stay inside the source root.'
    }
    Copy-PreviewSourceDirectory (Join-Path $previewSource $sourceDirectory)
}
# Build only the copied, reviewed extension/path selection. Internal App files,
# excluded feature registries, parent build hooks, bin and obj cannot fill a
# missing dependency and make the stripped preview appear self-contained.
$artifacts = Join-Path $previewOutput 'artifacts'
$buildProperties = @('-p:RuntimeIdentifier=win-x64', '-p:SelfContained=false',
    '-p:PublishSingleFile=false', '-p:NuGetAudit=false', '-p:DebugType=None', '-p:DebugSymbols=false')
$results = [Collections.Generic.List[object]]::new()
Write-Host 'SOURCE_PREVIEW: building real managed control/bridge source; no DLDR backend or Runtime is included.'
Write-Host "SDK: $sdk"
Write-Host "Local output: $previewOutput"

try {
    Push-Location -LiteralPath $buildSource
    try {
        $buildSdk = (& $dotnet --version | Select-Object -Last 1).Trim()
        if ($LASTEXITCODE -ne 0 -or $buildSdk -ne $sdk -or $buildSdk -ne $manifest.testedSdk) { throw 'Isolated build SDK differs from the pinned reviewed SDK.' }
        foreach ($project in $manifest.projects) {
            $absolute = Join-Path $buildSource $project.path
            & $dotnet build $absolute --configuration Release --artifacts-path $artifacts @buildProperties --nologo
            if ($LASTEXITCODE -ne 0) { throw "Build failed: $($project.path)" }
            $results.Add([pscustomobject]@{ project = $project.path; build = 'PASS'; test = 'NOT_RUN' })
        }
        if (-not $BuildOnly) {
            foreach ($project in $manifest.projects | Where-Object test) {
                $projectOutput = Join-Path (Join-Path $artifacts 'bin') $project.assembly
                $assemblies = @(Get-ChildItem -LiteralPath $projectOutput -Recurse -File -Filter ($project.assembly + '.dll'))
                if ($assemblies.Count -ne 1) { throw "Expected exactly one compiled test assembly for $($project.assembly)." }
                Write-Host "CONTRACT TESTS ONLY: $($project.assembly)"
                & $dotnet $assemblies[0].FullName
                if ($LASTEXITCODE -ne 0) { throw "Contract tests failed: $($project.assembly)" }
                ($results | Where-Object project -eq $project.path).test = 'PASS'
            }
        }
    } finally { Pop-Location }
    $summary = [ordered]@{
        schemaVersion = 1; releaseKind = 'SOURCE_PREVIEW'; previewVersion = $manifest.previewVersion; sdk = $sdk
        managedBuild = 'PASS'; contractTests = $(if ($BuildOnly) { 'NOT_RUN' } else { 'PASS' })
        realRuntimeTested = $false; displayProcessingIncluded = $false
        projects = @($results); copiedSourceFileCount = $copiedSources.Count
        sourceFiles = @($copiedSources); utc = [DateTimeOffset]::UtcNow.ToString('O')
    }
    $summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $previewOutput 'source-preview-build-result.json') -Encoding utf8
    Write-Host 'SOURCE_PREVIEW_BUILD=PASS'
    Write-Host 'REAL_DLDR=NOT_INCLUDED'
} catch {
    Write-Error -ErrorRecord $_
    exit 1
}
