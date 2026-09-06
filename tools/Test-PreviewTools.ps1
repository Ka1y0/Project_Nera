#requires -Version 7.0
[CmdletBinding()]
param([string]$SourceRoot = (Split-Path -Parent $PSScriptRoot))
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('NeraPreviewToolTests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$source = [IO.Path]::GetFullPath($SourceRoot)
$passed = 0
$failed = 0
$utf8 = [Text.UTF8Encoding]::new($false)

function New-Fixture([string]$Name) {
    $directory = Join-Path $testRoot $Name
    New-Item -ItemType Directory -Path $directory | Out-Null
    return $directory
}
function Write-Fixture([string]$Root, [string]$Relative, [string]$Text) {
    $path = Join-Path $Root $Relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
    [IO.File]::WriteAllText($path, $Text, $utf8)
}
function Run-Audit([string]$Script, [string]$Root) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    foreach ($argument in @('-NoProfile', '-File', (Join-Path $PSScriptRoot $Script), '-SourceRoot', $Root)) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new(); $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw 'TEST_CHILD_START_FAILED' }
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit(); $text = $stdout.GetAwaiter().GetResult(); $null = $stderr.GetAwaiter().GetResult()
        return [pscustomobject]@{ ExitCode = $process.ExitCode; Result = ($text | ConvertFrom-Json); Raw = $text }
    } finally { $process.Dispose() }
}
function Check([bool]$Value, [string]$Name) {
    if ($Value) { ++$script:passed; Write-Output "PASS $Name" }
    else { ++$script:failed; Write-Output "FAIL $Name" }
}
function Has-Code($Audit, [string]$Code) {
    return $Audit.Result.status -eq 'FAIL' -and @($Audit.Result.issues | Where-Object code -eq $Code).Count -gt 0
}
function Run-TestGit([string]$Root, [string[]]$GitArguments) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = (Get-Command git -CommandType Application | Select-Object -First 1).Source
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    foreach ($argument in @('-C', $Root, '-c', 'user.name=SourcePreviewTest', '-c', 'user.email=test@example.invalid',
        '-c', 'commit.gpgsign=false', '-c', ('core.hooksPath=' + (Join-Path $testRoot 'empty-hooks')))) { $start.ArgumentList.Add($argument) }
    foreach ($argument in $GitArguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new(); $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw 'TEST_GIT_START_FAILED' }
        $out = $process.StandardOutput.ReadToEndAsync(); $err = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit(); $null = $out.GetAwaiter().GetResult(); $null = $err.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw 'TEST_GIT_FAILED' }
    } finally { $process.Dispose() }
}
function New-LocalizationFixture([string]$Name) {
    $root = New-Fixture $Name
    $directory = Join-Path $root 'src/Nera.Localization/Resources'
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    foreach ($file in @('Strings.resx', 'Strings.zh-TW.resx', 'Strings.zh-CN.resx')) {
        Copy-Item -LiteralPath (Join-Path (Join-Path $source 'src/Nera.Localization/Resources') $file) -Destination (Join-Path $directory $file)
    }
    return $root
}

function New-PackageFixture([string]$Name) {
    $root = New-Fixture $Name
    Write-Fixture $root 'README.md' '# Source preview'
    $allowlist = @{ schemaVersion = 1; releaseKind = 'SOURCE_PREVIEW'; version = '0.5.0-alpha.2'
        files = @('README.md', 'PUBLIC_SOURCE_ALLOWLIST.json')
        generatedPackageMetadata = @('SOURCE_MANIFEST.json', 'SBOM.spdx.json') }
    Write-Fixture $root 'PUBLIC_SOURCE_ALLOWLIST.json' ($allowlist | ConvertTo-Json)
    $records = @($allowlist.files | ForEach-Object {
        $file = Get-Item -LiteralPath (Join-Path $root $_)
        @{ path = $_; bytes = $file.Length; sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            classification = 'REVIEWED_SOURCE_TEXT' }
    })
    $manifest = @{ schemaVersion = 1; releaseKind = 'SOURCE_PREVIEW'; version = $allowlist.version
        sourceFileCount = $records.Count; files = $records; generatedArchiveMetadata = $allowlist.generatedPackageMetadata
        runtimePayloadIncluded = $false; privateAdapterIncluded = $false; productBinaryIncluded = $false }
    Write-Fixture $root 'SOURCE_MANIFEST.json' ($manifest | ConvertTo-Json -Depth 6)
    $sbom = @{ spdxVersion = 'SPDX-2.3'; SPDXID = 'SPDXRef-DOCUMENT'; dataLicense = 'CC0-1.0'
        files = @($records | ForEach-Object { @{ fileName = './' + $_.path; checksums = @(@{ algorithm = 'SHA256'; checksumValue = $_.sha256 }) } }) }
    Write-Fixture $root 'SBOM.spdx.json' ($sbom | ConvertTo-Json -Depth 6)
    return $root
}

try {
    $clean = New-Fixture 'clean'
    Write-Fixture $clean 'README.md' '# Source preview'
    $audit = Run-Audit 'Test-SourceBoundary.ps1' $clean
    Check ($audit.ExitCode -eq 0 -and $audit.Result.status -eq 'PASS') 'clean source passes'

    $fixture = New-Fixture 'fixture'
    $synthetic = 'C' + ':\FakeRuntime\nvngx_dlssnr.dll'
    Write-Fixture $fixture 'src/Nera.Control.Tests/FakeControlBackend.cs' ('string value = @"' + $synthetic + '";')
    $audit = Run-Audit 'Test-SourceBoundary.ps1' $fixture
    Check ($audit.ExitCode -eq 0 -and $audit.Result.counts.syntheticFixtureMatches -eq 1) 'exact fixture counted separately'
    Write-Fixture $fixture 'src/Unexpected.cs' ('string value = @"' + $synthetic + '";')
    $audit = Run-Audit 'Test-SourceBoundary.ps1' $fixture
    Check ((Has-Code $audit 'PRIVATE_ABSOLUTE_PATH') -and $audit.ExitCode -eq 1) 'fixture exemption is path scoped'

    $secretRoot = New-Fixture 'credential'
    $fakeCredential = ('gh' + 'p_') + ('A' * 36)
    Write-Fixture $secretRoot 'example.txt' $fakeCredential
    $audit = Run-Audit 'Test-SourceBoundary.ps1' $secretRoot
    Check ((Has-Code $audit 'CREDENTIAL_PATTERN') -and $audit.ExitCode -eq 1) 'credential-shaped fixture rejected'
    Check (-not $audit.Raw.Contains($fakeCredential, [StringComparison]::Ordinal)) 'credential never appears in result'
    Write-Fixture $secretRoot ($fakeCredential + '.txt') 'safe text'
    $audit = Run-Audit 'Test-SourceBoundary.ps1' $secretRoot
    Check ((Has-Code $audit 'CREDENTIAL_IN_FILENAME') -and -not $audit.Raw.Contains($fakeCredential)) 'credential filename is rejected and redacted'

    $privateRoot = New-Fixture 'private-path'
    $fakePrivatePath = 'C' + ':\Users\SyntheticPerson\private.txt'
    Write-Fixture $privateRoot 'example.txt' $fakePrivatePath
    $audit = Run-Audit 'Test-SourceBoundary.ps1' $privateRoot
    Check ((Has-Code $audit 'PRIVATE_ABSOLUTE_PATH') -and -not $audit.Raw.Contains($fakePrivatePath)) 'private path rejected without matched content'

    $binaryRoot = New-Fixture 'renamed-binary'
    [IO.File]::WriteAllBytes((Join-Path $binaryRoot 'renamed.txt'), [byte[]](0x4d, 0x5a, 0, 1, 2, 3))
    $audit = Run-Audit 'Test-SourceBoundary.ps1' $binaryRoot
    Check (Has-Code $audit 'BINARY_PAYLOAD') 'renamed binary rejected by bytes'
    Write-Fixture $binaryRoot 'asset.png' 'not even an image'
    $audit = Run-Audit 'Test-SourceBoundary.ps1' $binaryRoot
    Check (Has-Code $audit 'FORBIDDEN_SOURCE_TREE_FILE') 'image extension rejected'

    $imageRoot = New-Fixture 'reviewed-image'
    $imageRelative = 'docs/media/nera-main-ui-en.png'
    $imagePath = Join-Path $imageRoot $imageRelative
    New-Item -ItemType Directory -Path (Split-Path -Parent $imagePath) -Force | Out-Null
    $imageBytes = [IO.File]::ReadAllBytes((Join-Path $source $imageRelative))
    [IO.File]::WriteAllBytes($imagePath, $imageBytes)
    $audit = Run-Audit 'Test-SourceBoundary.ps1' $imageRoot
    Check ($audit.ExitCode -eq 0) 'reviewed image exact path and bytes pass'
    $imageBytes[$imageBytes.Length - 1] = $imageBytes[$imageBytes.Length - 1] -bxor 1
    [IO.File]::WriteAllBytes($imagePath, $imageBytes)
    $audit = Run-Audit 'Test-SourceBoundary.ps1' $imageRoot
    Check (Has-Code $audit 'UNREVIEWED_DOCUMENTATION_IMAGE') 'changed image bytes require fresh review'
    [IO.File]::WriteAllBytes($imagePath, [byte[]](0x4d, 0x5a, 0, 1))
    $audit = Run-Audit 'Test-SourceBoundary.ps1' $imageRoot
    Check (Has-Code $audit 'UNREVIEWED_DOCUMENTATION_IMAGE') 'executable cannot use approved image path'
    $renamedImageRoot = New-Fixture 'renamed-reviewed-image'
    Copy-Item -LiteralPath (Join-Path $source $imageRelative) -Destination (Join-Path $renamedImageRoot 'other.png')
    $audit = Run-Audit 'Test-SourceBoundary.ps1' $renamedImageRoot
    Check (Has-Code $audit 'FORBIDDEN_SOURCE_TREE_FILE') 'reviewed image under another path rejected'

    $adapterRoot = New-Fixture 'private-adapter'
    Write-Fixture $adapterRoot 'src/Unsafe.cpp' (('NVSDK' + '_NGX_') + 'PrivateEntry();')
    $audit = Run-Audit 'Test-SourceBoundary.ps1' $adapterRoot
    Check (Has-Code $audit 'PRIVATE_ADAPTER_OPERATIONAL_SYMBOL') 'private operational symbol rejected'

    $historyRoot = New-Fixture 'history'
    New-Item -ItemType Directory -Path (Join-Path $testRoot 'empty-hooks') | Out-Null
    Run-TestGit $historyRoot @('init', '--quiet', '--template=')
    Write-Fixture $historyRoot 'README.md' $fakeCredential
    Run-TestGit $historyRoot @('add', '--', 'README.md')
    Run-TestGit $historyRoot @('commit', '--quiet', '-m', 'synthetic history fixture')
    Write-Fixture $historyRoot 'README.md' '# Source preview'
    Run-TestGit $historyRoot @('add', '--', 'README.md')
    Run-TestGit $historyRoot @('commit', '--quiet', '-m', 'remove synthetic fixture from working content')
    $audit = Run-Audit 'Test-SourceBoundary.ps1' $historyRoot
    Check ((Has-Code $audit 'CREDENTIAL_PATTERN') -and $audit.Result.history -eq 'INDEX_AND_ALL_REACHABLE_BLOBS_SCANNED') 'removed credential remains detected in reachable history'
    Check (-not $audit.Raw.Contains($fakeCredential)) 'git history matched content never emitted'

    $indexRoot = New-Fixture 'index-only'
    Run-TestGit $indexRoot @('init', '--quiet', '--template=')
    Write-Fixture $indexRoot 'README.md' $fakeCredential
    Run-TestGit $indexRoot @('add', '--', 'README.md')
    Write-Fixture $indexRoot 'README.md' '# clean working content, unsafe index'
    $audit = Run-Audit 'Test-SourceBoundary.ps1' $indexRoot
    Check ((Has-Code $audit 'CREDENTIAL_PATTERN') -and $audit.Result.counts.trackedEntries -eq 1) 'staged-only credential is detected'

    $allowlistRoot = New-Fixture 'allowlist'
    Write-Fixture $allowlistRoot 'README.md' '# Source preview'
    $manifest = @{ schemaVersion = 1; releaseKind = 'SOURCE_PREVIEW'; version = '0.5.0-alpha.2'; files = @('README.md', 'PUBLIC_SOURCE_ALLOWLIST.json') }
    Write-Fixture $allowlistRoot 'PUBLIC_SOURCE_ALLOWLIST.json' ($manifest | ConvertTo-Json)
    $audit = Run-Audit 'Test-SourceBoundary.ps1' $allowlistRoot
    Check ($audit.ExitCode -eq 0 -and $audit.Result.allowlist -eq 'EXACT_WORKING_FILE_SET_CHECKED') 'exact public allowlist passes'
    Write-Fixture $allowlistRoot 'extra.txt' 'not approved'
    $audit = Run-Audit 'Test-SourceBoundary.ps1' $allowlistRoot
    Check (Has-Code $audit 'FILE_NOT_IN_PUBLIC_ALLOWLIST') 'extra file cannot escape allowlist'

    $packageRoot = New-PackageFixture 'package-roundtrip'
    $archive = Join-Path $testRoot 'source-fixture.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($packageRoot, $archive)
    $extracted = Join-Path $testRoot 'package-extracted'
    [IO.Compression.ZipFile]::ExtractToDirectory($archive, $extracted)
    $audit = Run-Audit 'Test-SourceBoundary.ps1' $extracted
    Check ($audit.ExitCode -eq 0 -and $audit.Result.counts.packageSourceFilesVerified -eq 2 -and
        $audit.Result.packageMetadata -eq 'SOURCE_BYTES_AND_METADATA_CHECKED') 'source ZIP extraction verifies exact metadata and source hashes'
    Write-Fixture $extracted 'README.md' '# Source changed'
    $audit = Run-Audit 'Test-SourceBoundary.ps1' $extracted
    Check (Has-Code $audit 'PACKAGE_SOURCE_CHECKSUM_MISMATCH') 'extracted source checksum mismatch rejected'
    Write-Fixture $packageRoot 'unexpected-generated.json' '{}'
    $audit = Run-Audit 'Test-SourceBoundary.ps1' $packageRoot
    Check (Has-Code $audit 'FILE_NOT_IN_PUBLIC_ALLOWLIST') 'third generated metadata file rejected'

    $missingRoot = New-PackageFixture 'package-missing-record'
    $path = Join-Path $missingRoot 'SOURCE_MANIFEST.json'
    $package = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
    $package.files = @($package.files[0])
    Write-Fixture $missingRoot 'SOURCE_MANIFEST.json' ($package | ConvertTo-Json -Depth 6)
    $audit = Run-Audit 'Test-SourceBoundary.ps1' $missingRoot
    Check (Has-Code $audit 'INVALID_PACKAGE_SOURCE_MANIFEST') 'missing source manifest record rejected'

    $metadataSecretRoot = New-PackageFixture 'package-content-scanned'
    $path = Join-Path $metadataSecretRoot 'SBOM.spdx.json'
    $package = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
    $package.comment = $fakeCredential
    Write-Fixture $metadataSecretRoot 'SBOM.spdx.json' ($package | ConvertTo-Json -Depth 6)
    $audit = Run-Audit 'Test-SourceBoundary.ps1' $metadataSecretRoot
    Check ((Has-Code $audit 'CREDENTIAL_PATTERN') -and -not $audit.Raw.Contains($fakeCredential)) 'generated metadata scanned without exposing credential fixture'

    $invalidMetadataRoot = New-PackageFixture 'package-invalid-allowlist'
    $path = Join-Path $invalidMetadataRoot 'PUBLIC_SOURCE_ALLOWLIST.json'
    $package = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
    $package.generatedPackageMetadata += 'arbitrary.json'
    Write-Fixture $invalidMetadataRoot 'PUBLIC_SOURCE_ALLOWLIST.json' ($package | ConvertTo-Json -Depth 6)
    $audit = Run-Audit 'Test-SourceBoundary.ps1' $invalidMetadataRoot
    Check (Has-Code $audit 'INVALID_GENERATED_METADATA_ALLOWLIST') 'generated metadata authorization rejects arbitrary names'

    $metadataGitRoot = New-PackageFixture 'package-git-forbidden'
    Run-TestGit $metadataGitRoot @('init', '--quiet', '--template=')
    $audit = Run-Audit 'Test-SourceBoundary.ps1' $metadataGitRoot
    Check (Has-Code $audit 'PACKAGE_METADATA_FORBIDDEN_IN_GIT_TREE') 'generated metadata forbidden in Git working tree even when untracked'
    Run-TestGit $metadataGitRoot @('add', '--', 'SOURCE_MANIFEST.json', 'SBOM.spdx.json')
    Run-TestGit $metadataGitRoot @('commit', '--quiet', '-m', 'synthetic generated metadata fixture')
    Run-TestGit $metadataGitRoot @('rm', '--quiet', '--', 'SOURCE_MANIFEST.json', 'SBOM.spdx.json')
    Run-TestGit $metadataGitRoot @('commit', '--quiet', '-m', 'remove generated metadata from tree')
    $audit = Run-Audit 'Test-SourceBoundary.ps1' $metadataGitRoot
    Check (Has-Code $audit 'PACKAGE_METADATA_FORBIDDEN_IN_GIT') 'removed generated metadata remains forbidden in reachable history'

    $localization = New-LocalizationFixture 'localization-clean'
    $audit = Run-Audit 'Test-Localization.ps1' $localization
    Check ($audit.ExitCode -eq 0 -and $audit.Result.status -eq 'PASS') 'actual three-language resources pass'
    $path = Join-Path $localization 'src/Nera.Localization/Resources/Strings.zh-TW.resx'
    [xml]$xml = Get-Content -LiteralPath $path -Raw
    $node = $xml.SelectSingleNode('/root/data')
    $null = $node.ParentNode.RemoveChild($node); $xml.Save($path)
    $audit = Run-Audit 'Test-Localization.ps1' $localization
    Check (Has-Code $audit 'MISSING_KEY') 'missing translation rejected'

    $formatRoot = New-LocalizationFixture 'localization-format'
    $path = Join-Path $formatRoot 'src/Nera.Localization/Resources/Strings.zh-CN.resx'
    [xml]$xml = Get-Content -LiteralPath $path -Raw
    $node = @($xml.SelectNodes('/root/data/value') | Where-Object InnerText -match '\{0')[0]
    $node.InnerText = $node.InnerText.Replace('{0', '{9'); $xml.Save($path)
    $audit = Run-Audit 'Test-Localization.ps1' $formatRoot
    Check (Has-Code $audit 'FORMAT_ARGUMENT_MISMATCH') 'format argument drift rejected'

    $placeholderRoot = New-LocalizationFixture 'localization-placeholder'
    $path = Join-Path $placeholderRoot 'src/Nera.Localization/Resources/Strings.zh-CN.resx'
    [xml]$xml = Get-Content -LiteralPath $path -Raw
    $xml.SelectSingleNode('/root/data/value').InnerText = 'TO' + 'DO'; $xml.Save($path)
    $audit = Run-Audit 'Test-Localization.ps1' $placeholderRoot
    Check (Has-Code $audit 'PLACEHOLDER_TEXT') 'placeholder value rejected'

    Write-Output "PREVIEW_TOOL_TESTS pass=$passed fail=$failed"
    if ($failed -ne 0) { exit 1 }
} catch {
    Write-Output 'FAIL tool-test execution error; fixture contents withheld'
    exit 2
} finally {
    # The only recursive removal is this invocation's new, exact, validated temp root.
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if ($resolved.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolved) -match '^NeraPreviewToolTests-[a-f0-9]{32}$') {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
