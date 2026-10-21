<#
Sets up and runs the CAIVR live voice server (Tools/voice_server.py).

    powershell -ExecutionPolicy Bypass -File Tools\voice_server.ps1 setup    once: Python venv, CUDA PyTorch, VoxCPM2
    powershell -ExecutionPolicy Bypass -File Tools\voice_server.ps1 run      start the server (runs setup first if needed)
    powershell -ExecutionPolicy Bypass -File Tools\voice_server.ps1 check    is it up and healthy?

Options
    -Port 5111       port to listen on
    -Lan             listen on the local network too, so a Quest can reach this PC
    -NoPrewarm       skip voicing the scripted lines in the background at startup
    -Venv <folder>   where the Python environment lives (default %LOCALAPPDATA%\CAIVR\voice-venv,
                     outside the repo so Unity and git never see it; also CAIVR_VOICE_VENV)
    -CacheDir <dir>  where finished lines are kept (default %LOCALAPPDATA%\CAIVR\VoiceCache)
    anything else is passed to voice_server.py, for example:  run --steps 6

Needs an NVIDIA GPU and Python 3.10 to 3.12. No account or API key. The voice model (about 5 GB)
downloads from Hugging Face the first time the server starts.
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)][ValidateSet('setup', 'run', 'check')][string]$Command = 'run',
    [int]$Port = 5111,
    [switch]$Lan,
    [switch]$NoPrewarm,
    [string]$Venv = $(if ($env:CAIVR_VOICE_VENV) { $env:CAIVR_VOICE_VENV } else { Join-Path $env:LOCALAPPDATA 'CAIVR\voice-venv' }),
    [string]$CacheDir,
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$ServerArgs
)

$ErrorActionPreference = 'Stop'
$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
$Server = Join-Path $Here 'voice_server.py'
$Requirements = Join-Path $Here 'voice_server_requirements.txt'
$VenvPython = Join-Path $Venv 'Scripts\python.exe'
$TorchIndex = 'https://download.pytorch.org/whl/cu128'

function Fail($message) {
    Write-Host ""
    Write-Host "ERROR: $message" -ForegroundColor Red
    exit 1
}

function Run($exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { Fail "'$exe $($arguments -join ' ')' failed (exit $LASTEXITCODE)." }
}

# VoxCPM2 supports Python 3.10 to 3.12 only; 3.13 will not install.
function Find-Python {
    foreach ($version in '3.12', '3.11', '3.10') {
        $found = & py "-$version" -c "import sys; print(sys.executable)" 2>$null
        if ($LASTEXITCODE -eq 0 -and $found) { return $found.Trim() }
    }
    $plain = Get-Command python -ErrorAction SilentlyContinue
    if ($plain) {
        $v = & $plain.Source -c "import sys; print('%d.%d' % sys.version_info[:2])" 2>$null
        if ($v -in '3.10', '3.11', '3.12') { return $plain.Source }
    }
    return $null
}

function Setup {
    Write-Host "== CAIVR live voice: setup ==" -ForegroundColor Cyan

    if (-not (Get-Command nvidia-smi -ErrorAction SilentlyContinue)) {
        Fail ("No NVIDIA GPU found (nvidia-smi is missing). The live voice needs one. " +
              "Without it the game still works: it falls back to the baked voice automatically.")
    }

    $python = Find-Python
    if (-not $python) {
        Fail "Python 3.10, 3.11 or 3.12 is required (3.13 does not work with the voice model). Install 3.12 from python.org."
    }
    Write-Host "Python : $python"
    Write-Host "Env    : $Venv"

    if (-not (Test-Path $VenvPython)) {
        Run $python @('-m', 'venv', $Venv)
    }

    Run $VenvPython @('-m', 'pip', 'install', '--upgrade', 'pip')

    # The default PyPI build of PyTorch on Windows is CPU-only and far too slow, so ask for the CUDA build.
    Write-Host "Installing PyTorch (CUDA build, about 3 GB) ..." -ForegroundColor Cyan
    Run $VenvPython @('-m', 'pip', 'install', 'torch', 'torchaudio', '--index-url', $TorchIndex)

    Write-Host "Installing the voice model library ..." -ForegroundColor Cyan
    Run $VenvPython @('-m', 'pip', 'install', '-r', $Requirements)

    $check = & $VenvPython -c "import torch; print(torch.cuda.get_device_name(0) if torch.cuda.is_available() else 'NO_CUDA')"
    if ($LASTEXITCODE -ne 0 -or $check -eq 'NO_CUDA') {
        Fail ("PyTorch cannot see the GPU. Update the NVIDIA driver, then delete '$Venv' and run setup again.")
    }

    Write-Host ""
    Write-Host "Setup done. GPU: $check" -ForegroundColor Green
    Write-Host "Start the server with:  powershell -ExecutionPolicy Bypass -File Tools\voice_server.ps1 run"
}

function Check {
    try {
        $health = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/health" -TimeoutSec 5
        Write-Host "Voice server is up on port $Port" -ForegroundColor Green
        $health | Format-List | Out-String | Write-Host
    }
    catch {
        Fail "Nothing is answering on http://127.0.0.1:$Port. Start it with: powershell -ExecutionPolicy Bypass -File Tools\voice_server.ps1 run"
    }
}

function Start-Server {
    if (-not (Test-Path $VenvPython)) { Setup }

    $serverArguments = @($Server, '--port', $Port)
    if (-not $NoPrewarm) { $serverArguments += '--prewarm' }
    if ($Lan) { $serverArguments += @('--host', '0.0.0.0') }
    if ($CacheDir) { $serverArguments += @('--cache-dir', $CacheDir) }
    if ($ServerArgs) { $serverArguments += $ServerArgs }

    Write-Host "== CAIVR live voice: starting (Ctrl+C to stop) ==" -ForegroundColor Cyan
    Write-Host "The first start downloads the voice model (about 5 GB) and takes a few minutes."
    Run $VenvPython $serverArguments
}

switch ($Command) {
    'setup' { Setup }
    'check' { Check }
    'run'   { Start-Server }
}
