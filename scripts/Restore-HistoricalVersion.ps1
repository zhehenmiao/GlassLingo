#Requires -Version 7.0
[CmdletBinding(DefaultParameterSetName = 'Restore')]
param(
    [Parameter(Mandatory)][string]$Archive,
    [Parameter(ParameterSetName = 'Restore')][string]$Version,
    [Parameter(ParameterSetName = 'Restore')][string]$Collection,
    [Parameter(ParameterSetName = 'Restore')][string]$Destination = './restored-history',
    [Parameter(Mandatory, ParameterSetName = 'List')][switch]$List,
    [Parameter(Mandatory, ParameterSetName = 'Verify')][switch]$VerifyOnly
)
$ErrorActionPreference = 'Stop'
$zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Archive).Path)
try {
    $manifestEntry = $zip.GetEntry('archive-manifest.json')
    if ($null -eq $manifestEntry) { throw 'Archive manifest is missing.' }
    $reader = [IO.StreamReader]::new($manifestEntry.Open())
    try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ($manifest.format -ne 'glasslingo-history-v1') { throw 'Unsupported archive format.' }
    $objects = @{}
    foreach ($item in $manifest.collections) {
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($file in $item.files) {
            if ($file.sha256 -notmatch '^[a-f0-9]{64}$' -or $file.length -lt 0) { throw 'Invalid file digest or length.' }
            $path = [string]$file.path
            if ($path -match '(^|/)\.\.?(/|$)|[:\\]' -or $path.StartsWith('/') -or -not $seen.Add($path)) {
                throw "Unsafe or duplicate path in manifest: $path"
            }
            $entry = $zip.GetEntry('objects/' + $file.sha256)
            if ($null -eq $entry -or $entry.Length -ne $file.length) { throw "Missing or incomplete content: $path" }
            if ($objects.ContainsKey($file.sha256) -and $objects[$file.sha256] -ne $file.length) { throw 'Conflicting content lengths.' }
            $objects[$file.sha256] = $file.length
        }
    }
    if ($List) {
        $manifest.collections | Select-Object id, version, kind, @{n='Files';e={$_.files.Count}}, @{n='MiB';e={[math]::Round(($_.files | Measure-Object length -Sum).Sum/1MB,1)}}
        return
    }
    if ($VerifyOnly) {
        $count = 0
        foreach ($digest in $objects.Keys) {
            $stream = $zip.GetEntry('objects/' + $digest).Open()
            try { $actual = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() } finally { $stream.Dispose() }
            if ($actual -ne $digest) { throw "Archive content checksum failed: $digest" }
            $count++
        }
        Write-Output "Verified $count unique files across $($manifest.collections.Count) collections."
        return
    }
    if ($Collection) { $selected = @($manifest.collections | Where-Object id -eq $Collection) }
    elseif ($Version) { $selected = @($manifest.collections | Where-Object { $_.version -eq $Version -and $_.preferred }) }
    else { throw 'Specify -Version or -Collection; use -List to inspect available versions.' }
    if ($selected.Count -ne 1) { throw 'No unique collection matched; use -List and select its exact -Collection.' }
    $item = $selected[0]
    if ($item.id -notmatch '^[A-Za-z0-9._-]+$') { throw 'Unsafe collection name.' }
    $parent = [IO.Path]::GetFullPath($Destination)
    $target = [IO.Path]::GetFullPath((Join-Path $parent $item.id))
    if (-not $target.StartsWith($parent.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Restore destination escapes its parent.'
    }
    if (Test-Path -LiteralPath $target) { throw "Destination already exists: $target. Choose a fresh destination." }
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    foreach ($file in $item.files) {
        $outputPath = [IO.Path]::GetFullPath((Join-Path $target $file.path))
        if (-not $outputPath.StartsWith($target + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'File escapes destination.' }
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($outputPath)) | Out-Null
        $input = $zip.GetEntry('objects/' + $file.sha256).Open()
        try {
            $output = [IO.File]::Open($outputPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
            try { $input.CopyTo($output) } finally { $output.Dispose() }
        } finally { $input.Dispose() }
        if ((Get-FileHash -LiteralPath $outputPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.sha256) {
            throw "Restored file failed checksum: $($file.path). Do not run this incomplete restore."
        }
        [IO.File]::SetLastWriteTimeUtc($outputPath, [datetime]$file.lastWriteTimeUtc)
    }
    Write-Output "Restored and verified $($item.files.Count) files: $target"
} finally { $zip.Dispose() }
