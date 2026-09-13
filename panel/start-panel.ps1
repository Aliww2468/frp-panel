$ErrorActionPreference = 'Stop'
$panelUrl = 'http://127.0.0.1:17600'
try {
    try {
        $existing = Invoke-RestMethod "$panelUrl/api/state" -TimeoutSec 2
        if ($existing.revision -and $existing.proxies -is [Array]) {
            Start-Process $panelUrl
            exit 0
        }
    } catch { }
    $panelPython = $null
    $bundledPython = Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe'
    $candidates = @($bundledPython)
    $pythonCommand = Get-Command python.exe -ErrorAction SilentlyContinue
    if ($pythonCommand -and $pythonCommand.Source -notlike '*WindowsApps*') { $candidates += $pythonCommand.Source }
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            & $candidate -c 'import tomllib' 2>$null
            if ($LASTEXITCODE -eq 0) { $panelPython = $candidate; break }
        }
    }
    if (-not $panelPython) { throw 'Python 3.11 or newer is required.' }
    $panelScript = Join-Path $PSScriptRoot 'server.py'
    $panelProcess = Start-Process -FilePath $panelPython -ArgumentList @('"' + $panelScript + '"') -WorkingDirectory $PSScriptRoot -WindowStyle Hidden -PassThru
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        Start-Sleep -Milliseconds 300
        if ($panelProcess.HasExited) { throw 'Panel could not start. Check if port 17600 is occupied.' }
        try {
            $ready = Invoke-RestMethod "$panelUrl/api/state" -TimeoutSec 3
            if ($ready.revision) { Start-Process $panelUrl; exit 0 }
        } catch { }
    }
    throw 'Panel did not become ready at http://127.0.0.1:17600.'
} catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    Read-Host 'Press Enter to close'
    exit 1
}
