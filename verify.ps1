# Build, run unit tests, start the API, run the full e2e smoke suite, then stop the API.
# Usage: ./verify.ps1
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$project = Join-Path $root "morrow-notifications/morrow-notifications.csproj"
$testProject = Join-Path $root "MN.Tests/MN.Tests.csproj"
$base = "http://localhost:5252"
$appJob = $null

function Stop-App {
    if ($appJob) {
        Stop-Job $appJob -ErrorAction SilentlyContinue
        Remove-Job $appJob -Force -ErrorAction SilentlyContinue
    }
    Get-Process -Name "morrow-notifications" -ErrorAction SilentlyContinue | Stop-Process -Force
}

function Wait-ForApi {
    param([int]$TimeoutSeconds = 30)
    for ($i = 0; $i -lt $TimeoutSeconds; $i++) {
        try {
            $r = Invoke-WebRequest -Uri "$base/api/tenants" -UseBasicParsing -TimeoutSec 2
            if ($r.StatusCode -eq 200) { return $true }
        } catch {}
        Start-Sleep -Seconds 1
    }
    return $false
}

try {
    Write-Host "`n=== morrow-notifications verify ===" -ForegroundColor Yellow

    Write-Host "`n[1/4] Build..." -ForegroundColor Cyan
    Push-Location $root
    dotnet build $project --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }

    Write-Host "`n[2/4] Unit tests..." -ForegroundColor Cyan
    dotnet test $testProject --verbosity minimal --no-build
    if ($LASTEXITCODE -ne 0) { throw "Unit tests failed." }

    Write-Host "`n[3/4] Start API..." -ForegroundColor Cyan
    Stop-App
    $appJob = Start-Job -ScriptBlock {
        param($proj)
        Set-Location (Split-Path $proj -Parent) | Out-Null
        Set-Location ..
        dotnet run --project $proj --no-build --urls http://localhost:5252 2>&1
    } -ArgumentList $project

    if (-not (Wait-ForApi)) { throw "API did not become ready at $base within 30s." }
    Write-Host "     API ready at $base" -ForegroundColor Green

    Write-Host "`n[4/4] API smoke tests (run-e2e-tests.ps1)..." -ForegroundColor Cyan
    & (Join-Path $root "run-e2e-tests.ps1")
    if ($LASTEXITCODE -ne 0) { throw "E2E smoke tests failed." }

    Write-Host "`n=== ALL CHECKS PASSED ===" -ForegroundColor Green
}
catch {
    Write-Host "`n=== VERIFY FAILED ===" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
finally {
    Pop-Location -ErrorAction SilentlyContinue
    Stop-App
}
