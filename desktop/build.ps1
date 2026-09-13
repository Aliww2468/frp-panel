$ErrorActionPreference = 'Stop'
$desktopRoot = $PSScriptRoot
$appOutput = Join-Path $desktopRoot 'app'
$downloadDir = Join-Path $desktopRoot 'downloads'
New-Item -ItemType Directory -Force -Path $downloadDir | Out-Null
# Generate a native icon from simple geometry; no external art dependencies.
Add-Type -AssemblyName System.Drawing
$bitmap = New-Object Drawing.Bitmap 64,64
$graphics = [Drawing.Graphics]::FromImage($bitmap)
$graphics.Clear([Drawing.Color]::FromArgb(233,115,72))
$font = New-Object Drawing.Font 'Segoe UI',40,([Drawing.FontStyle]::Bold),([Drawing.GraphicsUnit]::Pixel)
$graphics.DrawString('f', $font, [Drawing.Brushes]::White, 20, 4)
$appIcon = [Drawing.Icon]::FromHandle($bitmap.GetHicon())
$iconStream = [IO.File]::Create((Join-Path $desktopRoot 'app.ico'))
$appIcon.Save($iconStream)
$iconStream.Dispose(); $graphics.Dispose(); $bitmap.Dispose(); $font.Dispose()
dotnet publish (Join-Path $desktopRoot 'FrpPanel.csproj') -c Release -r win-x64 --self-contained false -o $appOutput -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'Desktop build failed.' }
& (Join-Path $desktopRoot 'remove-legacy-runtime.ps1') -AppDirectory $appOutput
$pythonArchive = Join-Path $downloadDir 'python-3.12.10-embed-amd64.zip'
if (-not (Test-Path -LiteralPath $pythonArchive)) {
    Invoke-WebRequest 'https://www.python.org/ftp/python/3.12.10/python-3.12.10-embed-amd64.zip' -OutFile $pythonArchive
}
$pythonOutput = Join-Path $appOutput 'python'
Expand-Archive -LiteralPath $pythonArchive -DestinationPath $pythonOutput -Force
& (Join-Path $pythonOutput 'python.exe') -c 'import tomllib, http.server, urllib.request, hashlib; print(1)'
if ($LASTEXITCODE -ne 0) { throw 'Bundled Python validation failed.' }
Write-Host "Desktop ready: $appOutput\FrpPanel.exe"
$shortcutPath = Join-Path (Split-Path $desktopRoot -Parent) 'FRP 面板.lnk'
$shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcutPath)
$shortcut.TargetPath = Join-Path $appOutput 'FrpPanel.exe'
$shortcut.WorkingDirectory = Split-Path $desktopRoot -Parent
$shortcut.IconLocation = $shortcut.TargetPath + ',0'
$shortcut.Save()
