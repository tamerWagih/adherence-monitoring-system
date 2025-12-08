$ErrorActionPreference = "Stop"

function Write-Info([string]$msg) { Write-Host "[INFO] $msg" -ForegroundColor Cyan }
function Write-Warn([string]$msg) { Write-Host "[WARN] $msg" -ForegroundColor Yellow }

$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root "dist\AdherenceAgent"
if (Test-Path $dist) { Remove-Item -Recurse -Force $dist }
New-Item -ItemType Directory -Path $dist | Out-Null

Write-Info "Publishing Service..."
dotnet publish "$root\AdherenceAgent.Service\AdherenceAgent.Service.csproj" -c Release -r win-x64 `
    /p:PublishSingleFile=true /p:SelfContained=true /p:IncludeNativeLibrariesForSelfExtract=true /p:PublishTrimmed=false `
    -o "$dist\Service"

Write-Info "Publishing Tray..."
dotnet publish "$root\AdherenceAgent.Tray\AdherenceAgent.Tray.csproj" -c Release -r win-x64 `
    /p:PublishSingleFile=true /p:SelfContained=true /p:IncludeNativeLibrariesForSelfExtract=true /p:PublishTrimmed=false `
    -o "$dist\Tray"

Write-Info "Copying configs..."
Copy-Item "$root\AdherenceAgent.Service\appsettings.json" "$dist\Service\appsettings.json"
Copy-Item "$root\AdherenceAgent.Service\appsettings.json" "$dist\Tray\appsettings.json"

Write-Info "Writing install/uninstall scripts..."
$installScript = @'
param(
    [string]$ServiceName = "AdherenceAgentService",
    [string]$DisplayName = "Adherence Monitoring Agent",
    [string]$Description = "Monitors workstation activity and uploads adherence data.",
    [string]$ServiceExe = (Join-Path $PSScriptRoot "Service\AdherenceAgent.Service.exe"),
    [string]$LogsDir = (Join-Path $env:ProgramData "AdherenceAgent\logs")
)

Write-Host "[INFO] Ensuring data folders..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $LogsDir | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $env:ProgramData "AdherenceAgent") | Out-Null

if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
    Write-Host "[INFO] Creating service $ServiceName" -ForegroundColor Cyan
    sc.exe create "$ServiceName" binPath= "`"$ServiceExe`"" DisplayName= "`"$DisplayName`"" start= auto | Out-Null
    sc.exe description "$ServiceName" "$Description" | Out-Null
} else {
    Write-Host "[WARN] Service $ServiceName already exists; skipping create." -ForegroundColor Yellow
}

Write-Host "[INFO] Starting service..." -ForegroundColor Cyan
Start-Service -Name $ServiceName -ErrorAction SilentlyContinue

Write-Host "[INFO] (Optional) Launching Tray..." -ForegroundColor Cyan
Start-Process -FilePath (Join-Path $PSScriptRoot "Tray\AdherenceAgent.Tray.exe") -ErrorAction SilentlyContinue

Write-Host "[INFO] Done."
'@

$uninstallScript = @'
param(
    [string]$ServiceName = "AdherenceAgentService"
)
Write-Host "[INFO] Stopping service..." -ForegroundColor Cyan
Stop-Service -Name $ServiceName -ErrorAction SilentlyContinue
Write-Host "[INFO] Deleting service..." -ForegroundColor Cyan
sc.exe delete "$ServiceName" | Out-Null
Write-Host "[INFO] Done."
'@

Set-Content -Path (Join-Path $dist "install-service.ps1") -Value $installScript -Encoding UTF8
Set-Content -Path (Join-Path $dist "uninstall-service.ps1") -Value $uninstallScript -Encoding UTF8

Write-Info "Creating zip..."
Compress-Archive -Path "$dist\*" -DestinationPath "$root\dist\AdherenceAgent.zip" -Force

Write-Info "Package created at $dist and $root\dist\AdherenceAgent.zip"

