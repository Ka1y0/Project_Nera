#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$SourceRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$OutputPath,
    [int]$MaximumTextBytes = 8MB
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$boundaryRoot = [IO.Path]::GetFullPath($SourceRoot).TrimEnd('\', '/')
$issues = [Collections.Generic.List[object]]::new()
$workingPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$counts = [ordered]@{
    workingFiles = 0; trackedEntries = 0; reachableBlobs = 0; uniqueGitBlobsScanned = 0
    credentialMatches = 0; privatePathMatches = 0; syntheticFixtureMatches = 0
    forbiddenFiles = 0; binaryContentMatches = 0; privateAdapterMatches = 0; unreadableFiles = 0
    manifestMismatches = 0; packageMetadataFiles = 0; packageSourceFilesVerified = 0
}
$historyStatus = 'NO_GIT_METADATA'
$packageStatus = 'NOT_PRESENT'
$packageMetadataNames = @('SOURCE_MANIFEST.json', 'SBOM.spdx.json')
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$maxBytes = [Math]::Max(1024, $MaximumTextBytes)

function Add-BoundaryIssue([string]$Code, [string]$Path, [int]$Line = 0) {
    # Never include the matched content, exception message or raw git stderr.
    $safePath = [regex]::Replace($Path, '[\x00-\x1f\x7f]', '?')
    foreach ($pattern in $credentialPatterns) { $safePath = [regex]::Replace($safePath, $pattern, '[credential-redacted]') }
    if ($safePath.Length -gt 250) { $safePath = $safePath.Substring(0, 250) }
    $issues.Add([pscustomobject]@{ code = $Code; path = $safePath; line = $Line })
}

function Get-LineNumber([string]$Text, [int]$Offset) {
    return 1 + [regex]::Matches($Text.Substring(0, $Offset), "`n").Count
}

$allowedExtensions = @('.cs', '.csproj', '.resx', '.md', '.txt', '.json', '.ps1', '.sln', '.slnx',
    '.yml', '.yaml', '.xml', '.config', '.props', '.targets', '.h', '.hpp', '.c', '.cpp', '.cmake', '.sha256')
$allowedNames = @('.gitignore', '.gitattributes', '.editorconfig', 'LICENSE', 'NOTICE', 'CMakeLists.txt')
$forbiddenExtensions = @('.exe', '.dll', '.pdb', '.obj', '.lib', '.a', '.so', '.dylib', '.bin', '.zip',
    '.7z', '.rar', '.tar', '.gz', '.nupkg', '.dmp', '.png', '.jpg', '.jpeg', '.gif', '.ico', '.svg',
    '.bmp', '.jxr', '.tif', '.tiff', '.exr', '.webp', '.mp4', '.mov', '.mp3', '.wav', '.pfx', '.p12', '.key')
$forbiddenDirectoryNames = @('bin', 'obj', 'dist', 'Data', 'Logs', 'screenshots', 'artifacts',
    '.vs', 'node_modules', '.nuget', 'RHI', 'Magpie')
$forbiddenNames = @('runtime.local.json', 'onboarding.json', 'hotkeys.json', 'cookies', 'Login Data',
    '.env', 'id_rsa', 'id_ed25519', 'ExperimentalFeatureRegistry.cs', 'DynamicFeature18Runtime.cpp',
    'DynamicFeature18Runtime.h', 'CompatSupport.cpp', 'CompatSupport.h')

function Test-BoundaryName([string]$Path, [string]$Label) {
    $normalized = $Path.Replace('\', '/')
    $leaf = ($normalized -split '/')[-1]
    $extension = [IO.Path]::GetExtension($leaf)
    $segments = $normalized -split '/'
    $bad = $extension -in $forbiddenExtensions -or $leaf -in $forbiddenNames -or
        @($segments | Where-Object { $_ -in $forbiddenDirectoryNames }).Count -gt 0
    if (-not $bad -and $extension -notin $allowedExtensions -and $leaf -notin $allowedNames) { $bad = $true }
    if ($bad) { ++$counts.forbiddenFiles; Add-BoundaryIssue 'FORBIDDEN_SOURCE_TREE_FILE' $Label }
    foreach ($pattern in $credentialPatterns) {
        foreach ($match in [regex]::Matches($Path, $pattern)) {
            ++$counts.credentialMatches; Add-BoundaryIssue 'CREDENTIAL_IN_FILENAME' $Label
        }
    }
}

# Exact fixtures are allowed only at their actual contract-test source locations.
# Build strings in pieces so the scanner does not exempt itself or match its own policy.
$fixtures = @{
    'src/Nera.Control.Tests/FakeControlBackend.cs' = @(('C' + ':\FakeRuntime\nvngx_dlssnr.dll'))
    'src/Nera.Control.Tests/Phase13ControlAuditTests.cs' = @(('C' + ':\private-user\photo-secret.jpg'))
    'src/Nera.Control.Tests/Phase14NativeLifecycleHealthTests.cs' = @(('C' + ':\private\runtime.dll'))
}
$credentialPatterns = @(
    '(?<![A-Za-z0-9])gh[pousr]_[A-Za-z0-9]{30,}',
    '(?<![A-Za-z0-9])github_pat_[A-Za-z0-9_]{50,}',
    '(?<![A-Za-z0-9])hf_[A-Za-z0-9]{30,}',
    '(?<![A-Za-z0-9])sk-(?:proj-|svcacct-)?[A-Za-z0-9_-]{32,}',
    '(?<![A-Za-z0-9])(?:AKIA|ASIA)[A-Z0-9]{16}(?![A-Za-z0-9])',
    '(?<![A-Za-z0-9])AIza[0-9A-Za-z_-]{35}(?![A-Za-z0-9])',
    '(?<![A-Za-z0-9])xox[baprs]-[A-Za-z0-9-]{20,}',
    '-----BEGIN (?:RSA |EC |OPENSSH |DSA |ENCRYPTED )?PRIVATE KEY-----',
    '(?<![A-Za-z0-9_-])eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{10,}',
    '(?i)\b(?:api[_-]?key|access[_-]?token|client[_-]?secret|password|passwd|authorization)\b\s*["'']?\s*[:=]\s*["''][A-Za-z0-9_+/=.-]{16,}["'']'
)
$pathPatterns = @(
    '(?i)(?<![A-Za-z0-9_])[a-z]:[\\/]+[^\r\n"''<>|;\)\]\s]*',
    '(?<![A-Za-z0-9])/(?:Users|home)/[A-Za-z0-9_.-]+/[^\s"''<>]*',
    '(?<![A-Za-z0-9:\\])\\\\[A-Za-z0-9][A-Za-z0-9_.-]*\\[^\s"''<>]+'
)
$adapterPattern = '(?:' + ('NVSDK' + '_NGX_') + '|(?<![A-Za-z0-9_])' + ('DLSS' + 'NR\.') + '|' +
    ('DynamicGetModuleFile' + 'NameW') + '|' + ('InstallCaller' + 'Compatibility') + '|' +
    ('EXPERIMENTAL_CALLER' + '_COMPATIBILITY') + '|' + ('\bVirtual' + 'Protect\s*\(') + '|' +
    ('\bGetModuleFile' + 'NameW\s*\(') + '|' + ('nvngx' + '\.dll') + '|' + ('nvsdk' + '_ngx') + ')'
$codeExtensions = @('.cs', '.cpp', '.c', '.h', '.hpp', '.ps1', '.csproj', '.props', '.targets', '.cmake')

function Test-BoundaryBytes([byte[]]$Bytes, [string]$Path, [string]$Label) {
    if ($Bytes.Length -gt $maxBytes) { Add-BoundaryIssue 'OVERSIZED_SOURCE_REQUIRES_REVIEW' $Label; return }
    if ($Bytes -contains 0 -or
        ($Bytes.Length -ge 2 -and $Bytes[0] -eq 0x4d -and $Bytes[1] -eq 0x5a) -or
        ($Bytes.Length -ge 4 -and $Bytes[0] -eq 0x50 -and $Bytes[1] -eq 0x4b -and $Bytes[2] -eq 3 -and $Bytes[3] -eq 4) -or
        ($Bytes.Length -ge 4 -and $Bytes[0] -eq 0x7f -and $Bytes[1] -eq 0x45 -and $Bytes[2] -eq 0x4c -and $Bytes[3] -eq 0x46)) {
        ++$counts.binaryContentMatches; Add-BoundaryIssue 'BINARY_PAYLOAD' $Label; return
    }
    try { $text = $utf8.GetString($Bytes) }
    catch { ++$counts.binaryContentMatches; Add-BoundaryIssue 'NON_UTF8_SOURCE' $Label; return }
    foreach ($pattern in $credentialPatterns) {
        foreach ($match in [regex]::Matches($text, $pattern)) {
            ++$counts.credentialMatches; Add-BoundaryIssue 'CREDENTIAL_PATTERN' $Label (Get-LineNumber $text $match.Index)
        }
    }
    foreach ($pattern in $pathPatterns) {
        foreach ($match in [regex]::Matches($text, $pattern)) {
            $normalized = $match.Value.Replace('/', '\')
            while ($normalized.Contains('\\')) { $normalized = $normalized.Replace('\\', '\') }
            $normalized = $normalized.TrimEnd('\')
            $sourcePath = $Path.Replace('\', '/')
            if ($fixtures.ContainsKey($sourcePath) -and $normalized -cin $fixtures[$sourcePath]) {
                ++$counts.syntheticFixtureMatches
            } else {
                ++$counts.privatePathMatches; Add-BoundaryIssue 'PRIVATE_ABSOLUTE_PATH' $Label (Get-LineNumber $text $match.Index)
            }
        }
    }
    if ([IO.Path]::GetExtension($Path) -in $codeExtensions -or [IO.Path]::GetFileName($Path) -eq 'CMakeLists.txt') {
        foreach ($match in [regex]::Matches($text, $adapterPattern, [Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
            ++$counts.privateAdapterMatches; Add-BoundaryIssue 'PRIVATE_ADAPTER_OPERATIONAL_SYMBOL' $Label (Get-LineNumber $text $match.Index)
        }
    }
}

function Scan-WorkingDirectory([string]$Directory) {
    foreach ($entry in Get-ChildItem -LiteralPath $Directory -Force) {
        if ($Directory -eq $boundaryRoot -and $entry.Name -eq '.git') { continue }
        $relative = [IO.Path]::GetRelativePath($boundaryRoot, $entry.FullName).Replace('\', '/')
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            Add-BoundaryIssue 'REPARSE_POINT_REQUIRES_REVIEW' $relative; continue
        }
        if ($entry.PSIsContainer) {
            if ($entry.Name -eq '.git') { Add-BoundaryIssue 'NESTED_GIT_METADATA' $relative; continue }
            if ($entry.Name -in $forbiddenDirectoryNames) { Add-BoundaryIssue 'FORBIDDEN_SOURCE_TREE_DIRECTORY' $relative }
            Scan-WorkingDirectory $entry.FullName
        } else {
            ++$counts.workingFiles
            $null = $workingPaths.Add($relative)
            Test-BoundaryName $relative $relative
            try {
                if ($entry.Length -gt $maxBytes) { Add-BoundaryIssue 'OVERSIZED_SOURCE_REQUIRES_REVIEW' $relative; continue }
                Test-BoundaryBytes ([IO.File]::ReadAllBytes($entry.FullName)) $relative $relative
            } catch { ++$counts.unreadableFiles; Add-BoundaryIssue 'SOURCE_READ_FAILED' $relative }
        }
    }
}

function Add-ManifestIssue([string]$Code, [string]$Path) {
    ++$counts.manifestMismatches; Add-BoundaryIssue $Code $Path
}

function Test-GeneratedPackage($Manifest, $Expected) {
    try {
        $package = Get-Content -LiteralPath (Join-Path $boundaryRoot 'SOURCE_MANIFEST.json') -Raw | ConvertFrom-Json -AsHashtable
        if ($package.schemaVersion -ne 1 -or $package.releaseKind -ne 'SOURCE_PREVIEW' -or
            $package.version -cne $Manifest.version -or $package.files -isnot [array] -or
            $package.sourceFileCount -ne $Expected.Count -or $package.files.Count -ne $Expected.Count -or
            $package.runtimePayloadIncluded -isnot [bool] -or $package.runtimePayloadIncluded -or
            $package.privateAdapterIncluded -isnot [bool] -or $package.privateAdapterIncluded -or
            $package.productBinaryIncluded -isnot [bool] -or $package.productBinaryIncluded -or
            $package.generatedArchiveMetadata -isnot [array] -or $package.generatedArchiveMetadata.Count -ne 2 -or
            @($packageMetadataNames | Where-Object { $_ -cnotin $package.generatedArchiveMetadata }).Count -ne 0) {
            Add-ManifestIssue 'INVALID_PACKAGE_SOURCE_MANIFEST' 'SOURCE_MANIFEST.json'; return
        }
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($record in $package.files) {
            if ($record -isnot [System.Collections.IDictionary] -or $record.path -isnot [string] -or
                -not $Expected.Contains($record.path) -or -not $seen.Add($record.path)) {
                Add-ManifestIssue 'PACKAGE_SOURCE_FILE_SET_MISMATCH' 'SOURCE_MANIFEST.json'; continue
            }
            $bytes = 0L
            if (-not [long]::TryParse([string]$record.bytes, [ref]$bytes) -or $bytes -lt 0 -or
                $record.sha256 -isnot [string] -or $record.sha256 -cnotmatch '^[a-f0-9]{64}$' -or
                $record.classification -cne 'REVIEWED_SOURCE_TEXT') {
                Add-ManifestIssue 'INVALID_PACKAGE_SOURCE_RECORD' $record.path; continue
            }
            if (-not $workingPaths.Contains($record.path)) {
                Add-ManifestIssue 'PACKAGE_SOURCE_FILE_MISSING' $record.path; continue
            }
            $file = Get-Item -LiteralPath (Join-Path $boundaryRoot $record.path)
            $actualHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($file.Length -ne $bytes -or $actualHash -cne $record.sha256) {
                Add-ManifestIssue 'PACKAGE_SOURCE_CHECKSUM_MISMATCH' $record.path; continue
            }
            ++$counts.packageSourceFilesVerified
        }
        foreach ($item in $Expected) {
            if (-not $seen.Contains($item)) { Add-ManifestIssue 'PACKAGE_SOURCE_FILE_SET_MISMATCH' $item }
        }
        $sbom = Get-Content -LiteralPath (Join-Path $boundaryRoot 'SBOM.spdx.json') -Raw | ConvertFrom-Json -AsHashtable
        if ($sbom.spdxVersion -cne 'SPDX-2.3' -or $sbom.SPDXID -cne 'SPDXRef-DOCUMENT' -or
            $sbom.dataLicense -cne 'CC0-1.0' -or $sbom.files -isnot [array] -or $sbom.files.Count -ne $Expected.Count) {
            Add-ManifestIssue 'INVALID_PACKAGE_SBOM' 'SBOM.spdx.json'; return
        }
        $sbomPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($record in $sbom.files) {
            if ($record -isnot [System.Collections.IDictionary] -or $record.fileName -isnot [string] -or
                -not $record.fileName.StartsWith('./', [StringComparison]::Ordinal)) {
                Add-ManifestIssue 'INVALID_PACKAGE_SBOM_RECORD' 'SBOM.spdx.json'; continue
            }
            $relative = $record.fileName.Substring(2)
            if (-not $Expected.Contains($relative) -or -not $sbomPaths.Add($relative)) {
                Add-ManifestIssue 'PACKAGE_SBOM_FILE_SET_MISMATCH' 'SBOM.spdx.json'; continue
            }
            $hashes = @($record.checksums | Where-Object { $_.algorithm -ceq 'SHA256' })
            $sourceRecords = @($package.files | Where-Object { $_.path -ceq $relative })
            if ($hashes.Count -ne 1 -or $sourceRecords.Count -ne 1 -or
                $hashes[0].checksumValue -cne $sourceRecords[0].sha256) {
                Add-ManifestIssue 'PACKAGE_SBOM_CHECKSUM_MISMATCH' $relative
            }
        }
        $script:packageStatus = 'SOURCE_BYTES_AND_METADATA_CHECKED'
    } catch { Add-ManifestIssue 'PACKAGE_METADATA_PARSE_FAILED' 'package-metadata' }
}

function Test-PublicAllowlist {
    $path = Join-Path $boundaryRoot 'PUBLIC_SOURCE_ALLOWLIST.json'
    if (-not (Test-Path -LiteralPath $path)) {
        foreach ($name in $packageMetadataNames) {
            if ($workingPaths.Contains($name)) { Add-ManifestIssue 'PACKAGE_METADATA_NOT_AUTHORIZED' $name }
        }
        return 'NOT_PRESENT'
    }
    $manifest = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.releaseKind -ne 'SOURCE_PREVIEW' -or
        $manifest.files -isnot [array]) { throw 'INVALID_PUBLIC_ALLOWLIST' }
    $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $metadataAllowed = $false
    if ($null -ne $manifest.PSObject.Properties['generatedPackageMetadata']) {
        $metadataAllowed = $manifest.generatedPackageMetadata -is [array] -and
            $manifest.generatedPackageMetadata.Count -eq 2 -and
            @($packageMetadataNames | Where-Object { $_ -cnotin $manifest.generatedPackageMetadata }).Count -eq 0
        if (-not $metadataAllowed) { Add-ManifestIssue 'INVALID_GENERATED_METADATA_ALLOWLIST' 'PUBLIC_SOURCE_ALLOWLIST.json' }
    }
    foreach ($item in $manifest.files) {
        if ($item -isnot [string] -or [string]::IsNullOrWhiteSpace($item) -or
            [IO.Path]::IsPathRooted($item) -or $item.Contains('\') -or
            $item -match '(^|/)(\.\.?|\.git)(/|$)' -or $item -in $packageMetadataNames -or -not $expected.Add($item)) {
            ++$counts.manifestMismatches; Add-BoundaryIssue 'INVALID_OR_DUPLICATE_ALLOWLIST_PATH' 'PUBLIC_SOURCE_ALLOWLIST.json'; continue
        }
        if (-not $workingPaths.Contains($item)) { ++$counts.manifestMismatches; Add-BoundaryIssue 'ALLOWLIST_FILE_MISSING' $item }
    }
    foreach ($item in $workingPaths) {
        if (-not $expected.Contains($item) -and -not ($metadataAllowed -and $item -cin $packageMetadataNames)) {
            ++$counts.manifestMismatches; Add-BoundaryIssue 'FILE_NOT_IN_PUBLIC_ALLOWLIST' $item
        }
    }
    $presentMetadata = @($packageMetadataNames | Where-Object { $workingPaths.Contains($_) })
    $counts.packageMetadataFiles = $presentMetadata.Count
    if ($presentMetadata.Count -gt 0) {
        if (Test-Path -LiteralPath (Join-Path $boundaryRoot '.git')) {
            Add-ManifestIssue 'PACKAGE_METADATA_FORBIDDEN_IN_GIT_TREE' 'package-metadata'
        } elseif (-not $metadataAllowed) {
            Add-ManifestIssue 'PACKAGE_METADATA_NOT_AUTHORIZED' 'package-metadata'
        } elseif ($presentMetadata.Count -ne 2) {
            Add-ManifestIssue 'PACKAGE_METADATA_PAIR_INCOMPLETE' 'package-metadata'
        } else { Test-GeneratedPackage $manifest $expected }
    }
    return 'EXACT_WORKING_FILE_SET_CHECKED'
}

function Invoke-LocalGit([string[]]$Arguments, [switch]$Raw) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = (Get-Command git -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.ArgumentList.Add('-C'); $start.ArgumentList.Add($boundaryRoot)
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new(); $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw 'GIT_START_FAILED' }
        $errorTask = $process.StandardError.ReadToEndAsync()
        $buffer = [IO.MemoryStream]::new()
        try {
            $process.StandardOutput.BaseStream.CopyTo($buffer)
            $process.WaitForExit()
            $null = $errorTask.GetAwaiter().GetResult()
            if ($process.ExitCode -ne 0) { throw 'GIT_READ_FAILED' }
            $bytes = $buffer.ToArray()
            if ($Raw) { return ,$bytes }
            return $utf8.GetString($bytes)
        } finally { $buffer.Dispose() }
    } finally { $process.Dispose() }
}

function Scan-GitObjects {
    if (-not (Test-Path -LiteralPath (Join-Path $boundaryRoot '.git'))) { return }
    $top = (Invoke-LocalGit @('rev-parse', '--show-toplevel')).Trim()
    if (-not [IO.Path]::GetFullPath($top).TrimEnd('\', '/').Equals($boundaryRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'GIT_ROOT_MISMATCH'
    }
    $objects = @{}
    $index = Invoke-LocalGit @('ls-files', '--stage', '-z')
    foreach ($entry in $index.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries)) {
        if ($entry -match '^([0-9]+) ([a-f0-9]{40,64}) ([0-3])\t(.+)$') {
            ++$counts.trackedEntries
            $mode = $Matches[1]; $objectId = $Matches[2]; $stage = $Matches[3]; $path = $Matches[4]
            if ([IO.Path]::GetFileName($path) -in $packageMetadataNames) { Add-ManifestIssue 'PACKAGE_METADATA_FORBIDDEN_IN_GIT' ('index:' + $path) }
            if ($mode -in @('160000', '120000') -or $stage -ne '0') { Add-BoundaryIssue 'UNQUALIFIED_INDEX_ENTRY' ('index:' + $path); continue }
            $objects[$objectId] = $path
        } else { Add-BoundaryIssue 'UNREADABLE_INDEX_ENTRY' 'git-index' }
    }
    $reachable = Invoke-LocalGit @('rev-list', '--objects', '--all')
    foreach ($entry in $reachable -split "`n") {
        if ($entry.TrimEnd("`r") -match '^([a-f0-9]{40,64})(?: (.*))?$') {
            $objectId = $Matches[1]
            $path = if ($Matches.ContainsKey(2)) { $Matches[2] } else { '' }
            $kind = (Invoke-LocalGit @('cat-file', '-t', $objectId)).Trim()
            if ($kind -eq 'blob') {
                if ([IO.Path]::GetFileName($path) -in $packageMetadataNames) { Add-ManifestIssue 'PACKAGE_METADATA_FORBIDDEN_IN_GIT' ('history:' + $path) }
                ++$counts.reachableBlobs
                if (-not $objects.ContainsKey($objectId) -or [string]::IsNullOrWhiteSpace($objects[$objectId])) { $objects[$objectId] = $path }
            }
        }
    }
    foreach ($objectId in $objects.Keys) {
        $path = [string]$objects[$objectId]
        $label = 'git-blob:' + $objectId.Substring(0, 12) + ':' + $path
        Test-BoundaryName $path $label
        $sizeText = (Invoke-LocalGit @('cat-file', '-s', $objectId)).Trim()
        $size = 0L
        if (-not [long]::TryParse($sizeText, [ref]$size) -or $size -gt $maxBytes) { Add-BoundaryIssue 'OVERSIZED_GIT_BLOB' $label; continue }
        ++$counts.uniqueGitBlobsScanned
        $blobBytes = Invoke-LocalGit @('cat-file', 'blob', $objectId) -Raw
        Test-BoundaryBytes $blobBytes $path $label
        # A renamed generated metadata blob is not allowed to hide in reachable history.
        try {
            $candidate = $utf8.GetString($blobBytes) | ConvertFrom-Json -AsHashtable -ErrorAction Stop
            if ($candidate -is [System.Collections.IDictionary] -and
                (($candidate.Contains('generatedArchiveMetadata') -and $candidate.releaseKind -eq 'SOURCE_PREVIEW') -or
                 ($candidate.Contains('spdxVersion') -and $candidate.SPDXID -eq 'SPDXRef-DOCUMENT'))) {
                Add-ManifestIssue 'PACKAGE_METADATA_FORBIDDEN_IN_GIT' $label
            }
        } catch { } # Non-JSON source has already been scanned normally.
    }
    $script:historyStatus = 'INDEX_AND_ALL_REACHABLE_BLOBS_SCANNED'
}

$scanStage = 'VALIDATE_INPUT'
try {
    if (-not (Test-Path -LiteralPath $boundaryRoot -PathType Container)) { throw 'SOURCE_ROOT_MISSING' }
    if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
        $OutputPath = [IO.Path]::GetFullPath($OutputPath)
        if ($OutputPath.Equals($boundaryRoot, [StringComparison]::OrdinalIgnoreCase) -or
            $OutputPath.StartsWith($boundaryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'OUTPUT_MUST_BE_OUTSIDE_SOURCE'
        }
        if (Test-Path -LiteralPath $OutputPath) { throw 'OUTPUT_ALREADY_EXISTS' }
    }
    $scanStage = 'WORKING_FILES'
    Scan-WorkingDirectory $boundaryRoot
    $scanStage = 'ALLOWLIST'
    $manifestStatus = Test-PublicAllowlist
    $scanStage = 'GIT_OBJECTS'
    Scan-GitObjects
    $scanStage = 'SERIALIZE_RESULT'
    $result = [ordered]@{
        schemaVersion = 1; check = 'SOURCE_BOUNDARY'; status = $(if ($issues.Count -eq 0) { 'PASS' } else { 'FAIL' })
        history = $historyStatus; allowlist = $manifestStatus; packageMetadata = $packageStatus; counts = $counts; issues = @($issues)
        evidenceBoundary = 'Pattern/allowlist scan of source bytes, index and reachable blobs; not exhaustive secret detection, license clearance, unreferenced Git-object review or graphics qualification. Synthetic fixtures are counted separately; matched content is never emitted.'
    }
    $json = $result | ConvertTo-Json -Depth 8
    if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
        [IO.File]::WriteAllText($OutputPath, $json, [Text.UTF8Encoding]::new($false))
    }
    Write-Output $json
    if ($issues.Count -ne 0) { exit 1 }
    exit 0
} catch {
    # Do not reveal raw exception messages; filenames and matched data can be sensitive.
    $knownCodes = @('SOURCE_ROOT_MISSING', 'OUTPUT_MUST_BE_OUTSIDE_SOURCE', 'OUTPUT_ALREADY_EXISTS',
        'INVALID_PUBLIC_ALLOWLIST', 'GIT_START_FAILED', 'GIT_READ_FAILED', 'GIT_ROOT_MISMATCH')
    $safeCode = if ($_.Exception.Message -cin $knownCodes) { $_.Exception.Message } else { 'SCAN_COULD_NOT_COMPLETE' }
    $nativeCode = $null
    $cause = $_.Exception
    while ($null -ne $cause) {
        if ($cause -is [ComponentModel.Win32Exception]) { $nativeCode = $cause.NativeErrorCode; break }
        $cause = $cause.InnerException
    }
    [ordered]@{ schemaVersion = 1; check = 'SOURCE_BOUNDARY'; status = 'ERROR'; code = $safeCode;
        stage = $scanStage; scriptLine = $_.InvocationInfo.ScriptLineNumber; nativeCode = $nativeCode } | ConvertTo-Json -Compress
    exit 2
}
