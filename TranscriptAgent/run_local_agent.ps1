Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$python = Join-Path $root ".venv\Scripts\python.exe"

if (-not (Test-Path -LiteralPath $python)) {
    Write-Host "尚未建立本地 Python 環境，先執行安裝程序..." -ForegroundColor Yellow
    python -m venv (Join-Path $root ".venv")
    & $python -m pip install -r (Join-Path $root "requirements.txt")
}

if (-not (Test-Path -LiteralPath (Join-Path $root ".env"))) {
    Copy-Item -LiteralPath (Join-Path $root ".env.example") -Destination (Join-Path $root ".env")
    Write-Host "已建立 TranscriptAgent\.env，請填入 LiveKit API Key/Secret 後再執行。" -ForegroundColor Yellow
    exit 1
}

$envText = Get-Content -LiteralPath (Join-Path $root ".env") -Raw
if ($envText -match "replace_with_livekit_api_(key|secret)") {
    Write-Host "請先在 TranscriptAgent\.env 填入 LIVEKIT_API_KEY 與 LIVEKIT_API_SECRET。" -ForegroundColor Yellow
    exit 1
}
if ($envText -match "replace_with_yating_api_key") {
    Write-Host "請先在 TranscriptAgent\.env 填入 YATING_API_KEY。" -ForegroundColor Yellow
    exit 1
}

Push-Location $root
try {
    & $python src\agent.py start
} finally {
    Pop-Location
}
