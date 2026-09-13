param([Parameter(Mandatory = $true)][string]$AppDirectory)
$ErrorActionPreference = 'Stop'
$appRoot = [IO.Path]::GetFullPath($AppDirectory).TrimEnd('\') + '\'
if (-not (Test-Path -LiteralPath (Join-Path $appRoot 'FrpPanel.exe'))) { throw 'FRP Panel application directory required.' }
$obsoleteFiles = foreach ($line in Get-Content -LiteralPath (Join-Path $PSScriptRoot 'legacy-runtime-files.iss')) {
    if ($line -match '^Type: files; Name: "\{app\}\\desktop\\app\\([^"]+)"$') {
        $targetPath = [IO.Path]::GetFullPath((Join-Path $appRoot $Matches[1]))
        if (-not $targetPath.StartsWith($appRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Legacy file path escapes application directory.' }
        $targetPath
    } elseif ($line.Trim() -and -not $line.StartsWith(';')) { throw 'Invalid legacy runtime manifest entry.' }
}
# Validate the complete list before removing exact files. Never recurse into user data.
foreach ($obsoleteFile in $obsoleteFiles) {
    if (Test-Path -LiteralPath $obsoleteFile -PathType Leaf) { Remove-Item -LiteralPath $obsoleteFile }
}
