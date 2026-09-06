#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$SourceRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$OutputPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$localizationRoot = [IO.Path]::GetFullPath($SourceRoot).TrimEnd('\', '/')
$issues = [Collections.Generic.List[object]]::new()
$resources = @{}
$counts = [ordered]@{ resourceFiles = 0; baselineKeys = 0; translatedValues = 0; duplicateKeys = 0
    missingKeys = 0; extraKeys = 0; emptyValues = 0; placeholderMismatches = 0
    placeholderText = 0; untranslatedValues = 0; invalidFormats = 0 }

function Add-LocalizationIssue([string]$Code, [string]$Language, [string]$Key) {
    $issues.Add([pscustomobject]@{ code = $Code; language = $Language; key = $Key })
}

function Read-ResourceMap([string]$Path, [string]$Language) {
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create($Path, $settings)
    $map = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    try {
        $document = [Xml.XmlDocument]::new(); $document.XmlResolver = $null; $document.Load($reader)
        foreach ($node in $document.SelectNodes('/root/data')) {
            $key = $node.GetAttribute('name')
            $valueNode = $node.SelectSingleNode('value')
            if ([string]::IsNullOrWhiteSpace($key) -or $null -eq $valueNode) {
                Add-LocalizationIssue 'MALFORMED_RESOURCE' $Language $key; continue
            }
            if ($map.ContainsKey($key)) { ++$counts.duplicateKeys; Add-LocalizationIssue 'DUPLICATE_KEY' $Language $key; continue }
            $value = $valueNode.InnerText
            $map.Add($key, $value)
            if ([string]::IsNullOrWhiteSpace($value)) { ++$counts.emptyValues; Add-LocalizationIssue 'EMPTY_VALUE' $Language $key }
            if ($value -match '(?i)\b(?:TODO|FIXME|TBD|PLACEHOLDER)\b|lorem\s+ipsum') {
                ++$counts.placeholderText; Add-LocalizationIssue 'PLACEHOLDER_TEXT' $Language $key
            }
            # Parse actual .NET composite format syntax, including escaped braces.
            try { $null = [Text.CompositeFormat]::Parse($value) }
            catch { ++$counts.invalidFormats; Add-LocalizationIssue 'INVALID_COMPOSITE_FORMAT' $Language $key }
        }
    } finally { $reader.Dispose() }
    return ,$map
}

function Format-Arguments([string]$Value) {
    $escaped = $Value.Replace('{{', '').Replace('}}', '')
    $indices = @([regex]::Matches($escaped, '\{(?<index>\d+)(?:\s*,\s*-?\d+)?(?::[^}]*)?\}') |
        ForEach-Object { [int]$_.Groups['index'].Value } | Sort-Object)
    return ($indices -join ',')
}

try {
    if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
        $OutputPath = [IO.Path]::GetFullPath($OutputPath)
        if ($OutputPath.Equals($localizationRoot, [StringComparison]::OrdinalIgnoreCase) -or
            $OutputPath.StartsWith($localizationRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'OUTPUT_MUST_BE_OUTSIDE_SOURCE'
        }
        if (Test-Path -LiteralPath $OutputPath) { throw 'OUTPUT_ALREADY_EXISTS' }
    }
    $resourceDirectory = Join-Path $localizationRoot 'src/Nera.Localization/Resources'
    foreach ($entry in @(@{ Language = 'en-US'; File = 'Strings.resx' },
        @{ Language = 'zh-TW'; File = 'Strings.zh-TW.resx' }, @{ Language = 'zh-CN'; File = 'Strings.zh-CN.resx' })) {
        $resources[$entry.Language] = Read-ResourceMap (Join-Path $resourceDirectory $entry.File) $entry.Language
        ++$counts.resourceFiles
    }
    $baseline = $resources['en-US']; $counts.baselineKeys = $baseline.Count
    foreach ($language in @('zh-TW', 'zh-CN')) {
        $translated = $resources[$language]
        foreach ($key in $baseline.Keys) {
            if (-not $translated.ContainsKey($key)) { ++$counts.missingKeys; Add-LocalizationIssue 'MISSING_KEY' $language $key; continue }
            ++$counts.translatedValues
            if ((Format-Arguments $baseline[$key]) -cne (Format-Arguments $translated[$key])) {
                ++$counts.placeholderMismatches; Add-LocalizationIssue 'FORMAT_ARGUMENT_MISMATCH' $language $key
            }
            if ($translated[$key] -ceq $baseline[$key] -and $translated[$key] -match '[A-Za-z]' -and
                $translated[$key] -notmatch '[\p{IsCJKUnifiedIdeographs}]' -and
                $key -notin @('Language.EnUs', 'Parameter.HdrTitle')) {
                ++$counts.untranslatedValues; Add-LocalizationIssue 'UNTRANSLATED_ENGLISH_VALUE' $language $key
            }
        }
        foreach ($key in $translated.Keys) {
            if (-not $baseline.ContainsKey($key)) { ++$counts.extraKeys; Add-LocalizationIssue 'EXTRA_KEY' $language $key }
        }
    }
    $labels = @{ 'Language.EnUs' = 'English'; 'Language.ZhTw' = '繁體中文'; 'Language.ZhCn' = '简体中文' }
    foreach ($language in @('en-US', 'zh-TW', 'zh-CN')) {
        foreach ($key in $labels.Keys) {
            if (-not $resources[$language].ContainsKey($key) -or $resources[$language][$key] -cne $labels[$key]) {
                Add-LocalizationIssue 'LANGUAGE_LABEL_MISMATCH' $language $key
            }
        }
    }
    $result = [ordered]@{ schemaVersion = 1; check = 'LOCALIZATION_RESOURCES'
        status = $(if ($issues.Count -eq 0) { 'PASS' } else { 'FAIL' }); counts = $counts; issues = @($issues)
        evidenceBoundary = 'Actual resx key/value/format checks only. Does not claim GUI coverage, translation quality, DPI layout, screen-reader behavior or graphics acceptance.' }
    $json = $result | ConvertTo-Json -Depth 7
    if (-not [string]::IsNullOrWhiteSpace($OutputPath)) { [IO.File]::WriteAllText($OutputPath, $json, [Text.UTF8Encoding]::new($false)) }
    Write-Output $json
    if ($issues.Count -ne 0) { exit 1 }
    exit 0
} catch {
    Write-Output '{"schemaVersion":1,"check":"LOCALIZATION_RESOURCES","status":"ERROR","code":"AUDIT_COULD_NOT_COMPLETE"}'
    exit 2
}
