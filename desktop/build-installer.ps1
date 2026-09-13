param([string]$Compiler = (Join-Path $PSScriptRoot 'tools\inno\ISCC.exe'), [string]$Version = '1.1.1')
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must use major.minor.patch format.' }
$projectRoot = Split-Path $PSScriptRoot -Parent
$releaseDir = Join-Path $projectRoot 'release'
$payload = Join-Path $PSScriptRoot ('package\' + [Guid]::NewGuid().ToString('N'))
$appDir = Join-Path $payload 'desktop\app'
$downloads = Join-Path $PSScriptRoot 'downloads'
if (-not (Test-Path -LiteralPath $Compiler)) { throw 'Install Inno Setup 6 and pass -Compiler with the ISCC.exe path.' }
New-Item -ItemType Directory -Force -Path $appDir,$releaseDir,(Join-Path $payload 'panel'),(Join-Path $payload 'client'),(Join-Path $payload 'licenses') | Out-Null
dotnet publish (Join-Path $PSScriptRoot 'FrpPanel.csproj') -c Release -r win-x64 --self-contained true -o $appDir -p:DebugType=None -p:DebugSymbols=false "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }
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
& $Compiler ("/DPayloadDir=$payload") ("/DReleaseDir=$releaseDir") ("/DAppVersion=$Version") (Join-Path $PSScriptRoot 'installer.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$setup = Join-Path $releaseDir "FRP-Panel-Setup-$Version-x64.exe"
$digest = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
Set-Content -LiteralPath ($setup + '.sha256') -Value ($digest + '  ' + (Split-Path $setup -Leaf)) -Encoding ascii
Write-Output "Installer: $setup"
Write-Output "Payload: $payload"
