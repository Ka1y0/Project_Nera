#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$SourceRoot = (Split-Path -Parent $PSScriptRoot),
    [Parameter(Mandatory)][string]$OutputRoot
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath($SourceRoot).TrimEnd('\', '/')
$output = [IO.Path]::GetFullPath($OutputRoot).TrimEnd('\', '/')
if ($output.Equals($source, [StringComparison]::OrdinalIgnoreCase) -or
    $output.StartsWith($source + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Package output must be outside the source tree.'
}
if (Test-Path -LiteralPath $output) { throw 'Output already exists; choose a new destination. Nothing will be overwritten.' }
$allowlistPath = Join-Path $source 'PUBLIC_SOURCE_ALLOWLIST.json'
$allowlist = Get-Content -LiteralPath $allowlistPath -Raw | ConvertFrom-Json
if ($allowlist.schemaVersion -ne 1 -or $allowlist.releaseKind -ne 'SOURCE_PREVIEW' -or
    $allowlist.version -notmatch '^\d+\.\d+\.\d+-alpha\.\d+$') { throw 'Unexpected source-only manifest.' }
$names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($requiredNotice in @('LICENSE','LICENSE_AUDIT.md','THIRD_PARTY_NOTICES.md','PROVENANCE.md','SECURITY.md')) {
    if ($requiredNotice -notin $allowlist.files) { throw 'Reviewed license/provenance boundary is incomplete.' }
}
$license = Get-Content -LiteralPath (Join-Path $source 'LICENSE') -Raw
if (-not $license.StartsWith('MIT License') -or $license -notmatch 'Ka1y0 and Nera contributors') { throw 'License changed; repeat owner/provenance review.' }
foreach ($relative in $allowlist.files) {
    if ([IO.Path]::IsPathRooted($relative) -or $relative -match '(^|/)\.\.(/|$)|\\|[\x00-\x1f]') { throw 'Invalid source-relative path.' }
    if (-not $names.Add($relative)) { throw 'Duplicate allowlist entry.' }
    $entry = Get-Item -LiteralPath (Join-Path $source $relative) -Force
    if ($entry.PSIsContainer -or ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Only regular source files are allowed.' }
}
& (Join-Path $PSScriptRoot 'Test-SourceBoundary.ps1') -SourceRoot $source
if (-not $?) { throw 'Public source boundary failed.' }
& (Join-Path $PSScriptRoot 'Test-Localization.ps1') -SourceRoot $source
if (-not $?) { throw 'Localization audit failed.' }
New-Item -ItemType Directory -Path $output | Out-Null
$sourceFiles = [Collections.Generic.List[object]]::new()
$spdxFiles = [Collections.Generic.List[object]]::new()
$relationships = [Collections.Generic.List[object]]::new()
$relationships.Add([ordered]@{spdxElementId='SPDXRef-DOCUMENT';relationshipType='DESCRIBES';relatedSpdxElement='SPDXRef-NeraSource'})
$sha1List = [Collections.Generic.List[string]]::new()
$fileIndex = 0
foreach ($relative in $allowlist.files | Sort-Object -CaseSensitive) {
    $entry = Get-Item -LiteralPath (Join-Path $source $relative)
    $sha256 = (Get-FileHash -LiteralPath $entry.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $sha1 = (Get-FileHash -LiteralPath $entry.FullName -Algorithm SHA1).Hash.ToLowerInvariant()
    $sha1List.Add($sha1)
    $isScreenshot = $relative -ceq 'docs/media/nera-main-ui-en.png'
    $classification = if ($isScreenshot) { 'REVIEWED_PRODUCT_SCREENSHOT' } else { 'REVIEWED_SOURCE_TEXT' }
    $fileLicense = if ($isScreenshot) { 'NOASSERTION' } else { 'MIT' }
    $sourceFiles.Add([ordered]@{path=$relative;bytes=$entry.Length;sha256=$sha256;classification=$classification})
    $fileIndex++
    $spdxId = 'SPDXRef-File-' + $fileIndex.ToString('D4')
    $spdxFiles.Add([ordered]@{
        SPDXID=$spdxId;fileName='./'+$relative;fileTypes=@($(if ($isScreenshot) { 'IMAGE' } else { 'TEXT' }))
        checksums=@([ordered]@{algorithm='SHA256';checksumValue=$sha256},[ordered]@{algorithm='SHA1';checksumValue=$sha1})
        licenseConcluded=$fileLicense;licenseInfoInFiles=@($fileLicense);copyrightText='NOASSERTION'
    })
    $relationships.Add([ordered]@{spdxElementId='SPDXRef-NeraSource';relationshipType='CONTAINS';relatedSpdxElement=$spdxId})
}
$packageCodeText = (@($sha1List | Sort-Object -CaseSensitive) -join '')
$packageCode = [Convert]::ToHexString([Security.Cryptography.SHA1]::HashData([Text.Encoding]::UTF8.GetBytes($packageCodeText))).ToLowerInvariant()
$created = [DateTimeOffset]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
$sourceManifest = [ordered]@{
    schemaVersion=1;releaseKind='SOURCE_PREVIEW';version=$allowlist.version
    sourceFileCount=$sourceFiles.Count;files=@($sourceFiles)
    generatedArchiveMetadata=@('SOURCE_MANIFEST.json','SBOM.spdx.json')
    runtimePayloadIncluded=$false;privateAdapterIncluded=$false;productBinaryIncluded=$false
    checksumScope='Source files only; generated manifests exclude themselves. SHA256SUMS covers the entire final ZIP.'
}
$sbom = [ordered]@{
    spdxVersion='SPDX-2.3';dataLicense='CC0-1.0';SPDXID='SPDXRef-DOCUMENT'
    name='Nera-'+$allowlist.version+'-source-preview'
    documentNamespace='https://spdx.org/spdxdocs/Nera-source-preview-'+[Guid]::NewGuid().ToString('D')
    creationInfo=[ordered]@{creators=@('Tool: Nera-Source-Preview-Packager-1');created=$created}
    comment='Source-only SBOM. No product binaries, SDK implementation or NVIDIA Runtime. Build-only tools are described in THIRD_PARTY_NOTICES, not bundled. Generated SPDX and manifest metadata are outside the analyzed source file set.'
    packages=@([ordered]@{
        SPDXID='SPDXRef-NeraSource';name='Nera-source-preview';versionInfo=$allowlist.version
        downloadLocation='NOASSERTION';filesAnalyzed=$true
        packageVerificationCode=[ordered]@{packageVerificationCodeValue=$packageCode;packageVerificationCodeExcludedFiles=@('SBOM.spdx.json','SOURCE_MANIFEST.json')}
        licenseConcluded='MIT';licenseDeclared='MIT';copyrightText='Copyright (c) 2026 Ka1y0 and Nera contributors'
    })
    files=@($spdxFiles);relationships=@($relationships)
}
$utf8 = [Text.UTF8Encoding]::new($false)
$manifestJson = ($sourceManifest | ConvertTo-Json -Depth 12) + "`n"
$sbomJson = ($sbom | ConvertTo-Json -Depth 12) + "`n"
[IO.File]::WriteAllText((Join-Path $output 'SOURCE_MANIFEST.json'), $manifestJson, $utf8)
[IO.File]::WriteAllText((Join-Path $output 'SBOM.spdx.json'), $sbomJson, $utf8)
$archiveName = 'Nera-v'+$allowlist.version+'-source-preview.zip'
$archivePath = Join-Path $output $archiveName
$zip = [IO.Compression.ZipFile]::Open($archivePath, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($record in $sourceFiles) {
        $bytes = [IO.File]::ReadAllBytes((Join-Path $source $record.path))
        if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant() -ne $record.sha256) { throw 'Source changed during packaging.' }
        $entry = $zip.CreateEntry($record.path, [IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = [DateTimeOffset]::new(2026,9,4,0,0,0,[TimeSpan]::Zero)
        $stream = $entry.Open()
        try { $stream.Write($bytes) } finally { $stream.Dispose() }
    }
    foreach ($metadata in @(@{name='SOURCE_MANIFEST.json';json=$manifestJson},@{name='SBOM.spdx.json';json=$sbomJson})) {
        $entry = $zip.CreateEntry($metadata.name, [IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = [DateTimeOffset]::new(2026,9,4,0,0,0,[TimeSpan]::Zero)
        $stream = $entry.Open()
        try { $stream.Write($utf8.GetBytes($metadata.json)) } finally { $stream.Dispose() }
    }
} finally { $zip.Dispose() }
$zip = [IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    if ($zip.Entries.Count -ne $sourceFiles.Count + 2) { throw 'Unexpected archive entry count.' }
    foreach ($record in $sourceFiles) {
        $entry = $zip.GetEntry($record.path)
        if ($null -eq $entry -or $entry.Length -ne $record.bytes) { throw 'Archive entry mismatch.' }
        $stream = $entry.Open()
        try { $actual = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
        finally { $stream.Dispose() }
        if ($actual -ne $record.sha256) { throw 'Archive content hash mismatch.' }
    }
} finally { $zip.Dispose() }
$sumLines = foreach ($name in @($archiveName,'SBOM.spdx.json','SOURCE_MANIFEST.json')) {
    (Get-FileHash -LiteralPath (Join-Path $output $name) -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $name
}
[IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS.txt'), ($sumLines -join "`n") + "`n", $utf8)
[ordered]@{releaseKind='SOURCE_PREVIEW';sourceFiles=$sourceFiles.Count;archive=$archiveName;zipSha256=(Get-FileHash -LiteralPath $archivePath).Hash;archiveRoundTrip='PASS';productBinaryIncluded=$false;runtimePayloadIncluded=$false} |
    ConvertTo-Json -Depth 4 | Write-Output
