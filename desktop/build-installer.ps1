param([string]$Compiler = (Join-Path $PSScriptRoot 'tools\inno\ISCC.exe'), [string]$Version = '1.2.1')
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must use major.minor.patch format.' }
$projectRoot = Split-Path $PSScriptRoot -Parent
$releaseDir = Join-Path $projectRoot 'release'
$payload = Join-Path $PSScriptRoot ('package\' + [Guid]::NewGuid().ToString('N'))
$appDir = Join-Path $payload 'desktop\app'
$downloads = Join-Path $PSScriptRoot 'downloads'
if (-not (Test-Path -LiteralPath $Compiler)) { throw 'Install Inno Setup 6 and pass -Compiler with the ISCC.exe path.' }
New-Item -ItemType Directory -Force -Path $appDir,$releaseDir,(Join-Path $payload 'panel'),(Join-Path $payload 'client'),(Join-Path $payload 'licenses') | Out-Null
dotnet publish (Join-Path $PSScriptRoot 'FrpPanel.csproj') -c Release -r win-x64 --self-contained false -o $appDir -p:DebugType=None -p:DebugSymbols=false "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }
foreach ($line in Get-Content -LiteralPath (Join-Path $PSScriptRoot 'legacy-runtime-files.iss')) {
    if ($line -match '^Type: files; Name: "\{app\}\\desktop\\app\\([^"]+)"$' -and (Test-Path -LiteralPath (Join-Path $appDir $Matches[1]))) {
        throw 'Legacy cleanup would remove a file shipped by this build; update legacy-runtime-files.iss.'
    }
}
Expand-Archive -LiteralPath (Join-Path $downloads 'python-3.12.10-embed-amd64.zip') -DestinationPath (Join-Path $appDir 'python')
# Deliberate allowlist: never copy saved connections, tokens, logs or backups.
Copy-Item -LiteralPath (Join-Path $projectRoot 'panel\server.py') -Destination (Join-Path $payload 'panel')
Copy-Item -LiteralPath (Join-Path $projectRoot 'panel\static') -Destination (Join-Path $payload 'panel') -Recurse
Copy-Item -LiteralPath (Join-Path $projectRoot 'client\frpc.exe') -Destination (Join-Path $payload 'client')
Copy-Item -LiteralPath (Join-Path $projectRoot 'frp_0.71.0_windows_amd64\LICENSE') -Destination (Join-Path $payload 'licenses\FRP-LICENSE.txt')
$webviewPackage = Join-Path $env:USERPROFILE '.nuget\packages\microsoft.web.webview2\1.0.4191.47'
Copy-Item -LiteralPath (Join-Path $webviewPackage 'LICENSE.txt') -Destination (Join-Path $payload 'licenses\WebView2-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $webviewPackage 'NOTICE.txt') -Destination (Join-Path $payload 'licenses\WebView2-NOTICE.txt')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'PACKAGE-README.txt') -Destination (Join-Path $payload 'README.txt')
$bootstrapper = Join-Path $downloads 'MicrosoftEdgeWebview2Setup.exe'
$signature = Get-AuthenticodeSignature -LiteralPath $bootstrapper
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notlike '*Microsoft Corporation*') { throw 'WebView2 installer signature is invalid.' }
# Resolve an official, stable .NET 8 desktop installer at build time. It is NOT bundled.
# Validate Microsoft's SHA-512 and signature, then pin its URL and SHA-256 in Setup.
$runtimeMetadata = Invoke-RestMethod 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/8.0/releases.json'
$runtimeRelease = $runtimeMetadata.releases | Where-Object { $_.windowsdesktop -and $_.windowsdesktop.version -match '^8\.0\.\d+$' } | Select-Object -First 1
$runtimeFile = $runtimeRelease.windowsdesktop.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name.EndsWith('.exe') } | Select-Object -First 1
if (-not $runtimeFile -or $runtimeFile.url -notmatch '^https://builds\.dotnet\.microsoft\.com/dotnet/WindowsDesktop/') { throw 'Unexpected .NET download metadata.' }
$runtimeInstaller = Join-Path $downloads (Split-Path $runtimeFile.url -Leaf)
if (-not (Test-Path -LiteralPath $runtimeInstaller)) { Invoke-WebRequest $runtimeFile.url -OutFile $runtimeInstaller }
if ((Get-FileHash -LiteralPath $runtimeInstaller -Algorithm SHA512).Hash -ne $runtimeFile.hash) { throw '.NET installer SHA-512 mismatch.' }
$runtimeSignature = Get-AuthenticodeSignature -LiteralPath $runtimeInstaller
if ($runtimeSignature.Status -ne 'Valid' -or $runtimeSignature.SignerCertificate.Subject -notlike '*Microsoft Corporation*') { throw '.NET installer signature is invalid.' }
$runtimeHash = (Get-FileHash -LiteralPath $runtimeInstaller -Algorithm SHA256).Hash
& $Compiler ("/DPayloadDir=$payload") ("/DReleaseDir=$releaseDir") ("/DAppVersion=$Version") ("/DDotNetUrl=$($runtimeFile.url)") ("/DDotNetHash=$runtimeHash") (Join-Path $PSScriptRoot 'installer.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$setup = Join-Path $releaseDir "FRP-Panel-Setup-$Version-x64.exe"
$digest = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
Set-Content -LiteralPath ($setup + '.sha256') -Value ($digest + '  ' + (Split-Path $setup -Leaf)) -Encoding ascii
Write-Output "Installer: $setup"
Write-Output "Payload: $payload"
