# Build WiX Installer for Adherence Agent
# Requires: WiX Toolset (https://wixtoolset.org/) installed and in PATH

$ErrorActionPreference = "Stop"

function Write-Info([string]$msg) { Write-Host "[INFO] $msg" -ForegroundColor Cyan }
function Write-Warn([string]$msg) { Write-Host "[WARN] $msg" -ForegroundColor Yellow }
function Write-Error([string]$msg) { Write-Host "[ERROR] $msg" -ForegroundColor Red }

# Check if WiX is installed and find it
$wixPath = Get-Command "candle.exe" -ErrorAction SilentlyContinue
if (-not $wixPath) {
    Write-Warn "WiX Toolset not found in PATH. Searching common installation locations..."
    
    # Common WiX installation paths
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
    
    $foundPath = $null
    foreach ($path in $commonPaths) {
        $candlePath = Join-Path $path "candle.exe"
        if (Test-Path $candlePath) {
            $foundPath = $path
            Write-Info "Found WiX Toolset at: $foundPath"
            # Add to PATH for this session
            $env:PATH = "$foundPath;$env:PATH"
            break
        }
    }
    
    if (-not $foundPath) {
        Write-Error "WiX Toolset not found. Please install from https://wixtoolset.org/"
        Write-Error "Or install via Chocolatey: choco install wixtoolset"
        Write-Error "After installation, restart PowerShell or add WiX bin directory to PATH"
        exit 1
    }
} else {
    Write-Info "Found WiX Toolset in PATH: $($wixPath.Source)"
}

$wixBin = if ($foundPath) { $foundPath } else { Split-Path -Parent $wixPath.Source }
$wixUtilExtDll = Join-Path $wixBin "WixUtilExtension.dll"
$wixUiExtDll = Join-Path $wixBin "WixUIExtension.dll"

$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root "dist\AdherenceAgent"
$installerDir = Join-Path $root "installer"
$buildDir = Join-Path $installerDir "build"
$outputDir = Join-Path $root "dist"
$licenseRtfPath = Join-Path $installerDir "License.rtf"

# Derive product version from the WiX source so we don't forget to update it here.
# Also keep MSI output name unique to avoid file-lock issues (Explorer/installer can lock .msi).
$wxsFile = Join-Path $installerDir "AdherenceAgent.wxs"
$productVersion = "0.0.0"
try {
    $wxsText = Get-Content -Raw $wxsFile
    $m = [regex]::Match($wxsText, '\<\?define\s+ProductVersion\s*=\s*\"([^\"]+)\"\s*\?\>')
    if ($m.Success) { $productVersion = $m.Groups[1].Value }
} catch { }
if (-not $productVersion) { $productVersion = "0.0.0" }
Write-Info "Using ProductVersion: $productVersion"

# Clean previous builds
if (Test-Path $dist) { Remove-Item -Recurse -Force $dist }
if (Test-Path $buildDir) { Remove-Item -Recurse -Force $buildDir }
New-Item -ItemType Directory -Path $dist -Force | Out-Null
New-Item -ItemType Directory -Path $buildDir -Force | Out-Null
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

Write-Info "Publishing Service..."
$serviceBinPath = Join-Path $dist "Service"
dotnet restore "$root\AdherenceAgent.Service\AdherenceAgent.Service.csproj" -r win-x64 | Out-Null
dotnet publish "$root\AdherenceAgent.Service\AdherenceAgent.Service.csproj" -c Release -r win-x64 `
    /p:PublishSingleFile=true /p:SelfContained=true /p:IncludeNativeLibrariesForSelfExtract=true /p:PublishTrimmed=false `
    -o $serviceBinPath

Write-Info "Publishing Tray..."
$trayBinPath = Join-Path $dist "Tray"
dotnet restore "$root\AdherenceAgent.Tray\AdherenceAgent.Tray.csproj" -r win-x64 | Out-Null
dotnet publish "$root\AdherenceAgent.Tray\AdherenceAgent.Tray.csproj" -c Release -r win-x64 `
    /p:PublishSingleFile=true /p:SelfContained=true /p:IncludeNativeLibrariesForSelfExtract=true /p:PublishTrimmed=false `
    -o $trayBinPath

Write-Info "Copying configs..."
if (Test-Path "$root\AdherenceAgent.Service\appsettings.json") {
    if (Test-Path $serviceBinPath) {
        Copy-Item "$root\AdherenceAgent.Service\appsettings.json" "$serviceBinPath\appsettings.json" -ErrorAction SilentlyContinue
    }
    if (Test-Path $trayBinPath) {
        Copy-Item "$root\AdherenceAgent.Service\appsettings.json" "$trayBinPath\appsettings.json" -ErrorAction SilentlyContinue
    }
} else {
    Write-Warn "appsettings.json not found, skipping config copy"
}

Write-Info "Compiling WiX installer..."
# Set WiX variables for paths
# WiX variables must be in format: -dVariableName=Value (no space after -d)

# Compile WiX source (.wxs -> .wixobj)
$wixObjFile = Join-Path $buildDir "AdherenceAgent.wixobj"

$candleArgs = @(
    "-nologo",
    "-out", "`"$wixObjFile`"",
    "-ext", "`"$wixUtilExtDll`"",
    "-dServiceBinPath=`"$serviceBinPath`"",
    "-dTrayBinPath=`"$trayBinPath`"",
    "-dLicenseRtfPath=`"$licenseRtfPath`"",
    "`"$wxsFile`""
)

& candle.exe $candleArgs
if ($LASTEXITCODE -ne 0) {
    Write-Error "WiX compilation failed"
    exit 1
}

# Link WiX object (.wixobj -> .msi)
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$msiFile = Join-Path $outputDir ("AdherenceAgent-" + $productVersion + "-" + $stamp + ".msi")
$lightArgs = @(
    "-nologo",
    "-out", "`"$msiFile`"",
    "-ext", "`"$wixUtilExtDll`"",
    "-ext", "`"$wixUiExtDll`"",
    "-cultures:en-US",
    "`"$wixObjFile`""
)

& light.exe $lightArgs
if ($LASTEXITCODE -ne 0) {
    Write-Error "WiX linking failed"
    exit 1
}

Write-Info "Installer created successfully: $msiFile"
Write-Info ""
Write-Info "To install:"
Write-Info "  msiexec /i `"$msiFile`" /quiet"
Write-Info ""
Write-Info "To uninstall:"
Write-Info "  msiexec /x `"$msiFile`" /quiet"
