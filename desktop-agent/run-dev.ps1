# Development run script for Service and Tray App
# This script helps run both components in development mode

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("service", "tray", "both")]
    [string]$Component = "both",
    
    [Parameter(Mandatory=$false)]
    [string]$WorkstationId = "",
    
    [Parameter(Mandatory=$false)]
    [string]$ApiKey = ""
)

$ErrorActionPreference = "Stop"

Write-Host "=== Adherence Agent Development Runner ===" -ForegroundColor Cyan
Write-Host ""

# Check if credentials need to be set
if ($Component -eq "service" -or $Component -eq "both") {
    if ($WorkstationId -and $ApiKey) {
        Write-Host "Setting credentials..." -ForegroundColor Yellow
        dotnet run --project AdherenceAgent.Service -- --set-creds $WorkstationId $ApiKey
        if ($LASTEXITCODE -ne 0) {
            Write-Host "Failed to set credentials!" -ForegroundColor Red
            exit 1
        }
        Write-Host "Credentials set successfully!" -ForegroundColor Green
        Write-Host ""
    }
}

# Check if processes are already running
$running = Get-Process | Where-Object {$_.ProcessName -like "*AdherenceAgent*"}
if ($running) {
    Write-Host "⚠️  Warning: AdherenceAgent processes are already running:" -ForegroundColor Yellow
    $running | ForEach-Object { Write-Host "  - $($_.ProcessName) (PID: $($_.Id))" -ForegroundColor Gray }
    Write-Host ""
    $response = Read-Host "Do you want to continue anyway? (y/n)"
    if ($response -ne "y") {
        exit 0
    }
}

Write-Host "Starting components..." -ForegroundColor Cyan
Write-Host ""

if ($Component -eq "service" -or $Component -eq "both") {
    Write-Host "=== Starting Service ===" -ForegroundColor Green
    Write-Host "Logs: %ProgramData%\AdherenceAgent\logs\agent.log" -ForegroundColor Gray
    Write-Host ""
    
    if ($Component -eq "both") {
        # Start service in background
        Start-Process powershell -ArgumentList "-NoExit", "-Command", "cd '$PWD'; dotnet run --project AdherenceAgent.Service"
        Start-Sleep -Seconds 2
    } else {
        # Run service in foreground
        dotnet run --project AdherenceAgent.Service
    }
}

if ($Component -eq "tray" -or $Component -eq "both") {
    Write-Host "=== Starting Tray App ===" -ForegroundColor Green
    Write-Host "Logs: %ProgramData%\AdherenceAgent\logs\tray.log" -ForegroundColor Gray
    Write-Host ""
    
    if ($Component -eq "both") {
        # Start tray in background
        Start-Process powershell -ArgumentList "-NoExit", "-Command", "cd '$PWD'; dotnet run --project AdherenceAgent.Tray"
    } else {
        # Run tray in foreground
        dotnet run --project AdherenceAgent.Tray
    }
}

if ($Component -eq "both") {
    Write-Host ""
    Write-Host "✅ Both components started in separate windows!" -ForegroundColor Green
    Write-Host ""
    Write-Host "To check logs:" -ForegroundColor Cyan
    Write-Host "  Get-Content `"$env:ProgramData\AdherenceAgent\logs\agent.log`" -Tail 50 -Wait" -ForegroundColor Gray
    Write-Host "  Get-Content `"$env:ProgramData\AdherenceAgent\logs\tray.log`" -Tail 50 -Wait" -ForegroundColor Gray
    Write-Host ""
    Write-Host "To check for duplicates:" -ForegroundColor Cyan
    Write-Host "  .\check-duplicates.ps1" -ForegroundColor Gray
}

