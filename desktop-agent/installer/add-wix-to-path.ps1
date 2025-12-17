# Script to add WiX Toolset to PATH permanently
# Run this as Administrator if you want to add it system-wide

$ErrorActionPreference = "Stop"

# Find WiX installation
$commonPaths = @(
    "${env:ProgramFiles(x86)}\WiX Toolset v3.14\bin",
    "${env:ProgramFiles(x86)}\WiX Toolset v3.13\bin",
    "${env:ProgramFiles(x86)}\WiX Toolset v3.12\bin",
    "${env:ProgramFiles(x86)}\WiX Toolset v3.11\bin",
    "${env:ProgramFiles}\WiX Toolset v3.14\bin",
    "${env:ProgramFiles}\WiX Toolset v3.13\bin",
    "${env:ProgramFiles}\WiX Toolset v3.12\bin",
    "${env:ProgramFiles}\WiX Toolset v3.11\bin"
)

$wixPath = $null
foreach ($path in $commonPaths) {
    if (Test-Path (Join-Path $path "candle.exe")) {
        $wixPath = $path
        break
    }
}

if (-not $wixPath) {
    Write-Host "WiX Toolset not found in common locations." -ForegroundColor Red
    Write-Host "Please install WiX Toolset first: choco install wixtoolset" -ForegroundColor Yellow
    exit 1
}

Write-Host "Found WiX Toolset at: $wixPath" -ForegroundColor Green

# Check if already in PATH
$currentPath = [Environment]::GetEnvironmentVariable("Path", "User")
if ($currentPath -like "*$wixPath*") {
    Write-Host "WiX Toolset is already in your user PATH." -ForegroundColor Yellow
    exit 0
}

# Add to user PATH
try {
    $newPath = $currentPath + ";$wixPath"
    [Environment]::SetEnvironmentVariable("Path", $newPath, "User")
    Write-Host "Successfully added WiX Toolset to user PATH!" -ForegroundColor Green
    Write-Host "Please restart PowerShell for changes to take effect." -ForegroundColor Yellow
} catch {
    Write-Host "Failed to add to PATH: $_" -ForegroundColor Red
    Write-Host "You may need to run this script as Administrator for system-wide PATH." -ForegroundColor Yellow
    exit 1
}
