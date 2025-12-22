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
Write-Info "Base ProductVersion (from .wxs): $productVersion"

# Auto-version each build to avoid "same version" upgrade/uninstall problems.
# MSI version format: Major.Minor.Build.Revision, where each part is 0..65535.
# We keep Major.Minor.Patch from the .wxs and set Revision to a build number.
#
# If you want to force a specific version, set environment variable:
#   $env:ADHERENCE_PRODUCT_VERSION = "1.0.15.123"
$forcedVersion = $env:ADHERENCE_PRODUCT_VERSION
if ($forcedVersion) {
    $newVersion = $forcedVersion
    Write-Info "Using forced ProductVersion from ADHERENCE_PRODUCT_VERSION: $newVersion"
} else {
    $parts = $productVersion.Split('.')
    $major = if ($parts.Length -ge 1) { $parts[0] } else { "1" }
    $minor = if ($parts.Length -ge 2) { $parts[1] } else { "0" }
    $patch = if ($parts.Length -ge 3) { $parts[2] } else { "0" }

    $build = 0
    try {
        $cnt = (& git rev-list --count HEAD 2>$null).Trim()
        if ($cnt) { $build = ([int]$cnt) % 65535 }
    } catch { }
    if ($build -le 0) {
        # Fallback: day-of-year * 100 + hour fits under 65535
        $now = Get-Date
        $build = (($now.DayOfYear * 100) + $now.Hour) % 65535
    }

    $newVersion = "$major.$minor.$patch.$build"
    Write-Info "Auto-bumped ProductVersion for this build: $newVersion"
}

$wxsFileToCompile = $wxsFile
$productVersion = $newVersion

# Clean previous builds
if (Test-Path $dist) { Remove-Item -Recurse -Force $dist }
if (Test-Path $buildDir) { Remove-Item -Recurse -Force $buildDir }
New-Item -ItemType Directory -Path $dist -Force | Out-Null
New-Item -ItemType Directory -Path $buildDir -Force | Out-Null
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

# Generate a temporary .wxs with the updated ProductVersion so we don't modify the repo file.
$generatedWxsFile = Join-Path $buildDir "AdherenceAgent.generated.wxs"
try {
    $generatedText = $wxsText
    $generatedText = [regex]::Replace(
        $generatedText,
        '(\<\?define\s+ProductVersion\s*=\s*\")([^\"]+)(\"\s*\?\>)',
        ('${1}' + $newVersion + '${3}')
    )
    Set-Content -Path $generatedWxsFile -Value $generatedText -Encoding UTF8
    $wxsFileToCompile = $generatedWxsFile
} catch {
    Write-Warn "Failed to generate versioned .wxs; falling back to original .wxs"
    $wxsFileToCompile = $wxsFile
}

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
    "`"$wxsFileToCompile`""
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
