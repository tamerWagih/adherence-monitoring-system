# Adherence Agent WiX Installer

This installer packages both the Windows Service and Tray Application into a single MSI installer.

## Features

- **Service Installation**: Installs as Windows Service running as SYSTEM account
- **Service ACL**: Only administrators can stop/start the service; non-admin users can only query status
- **Tray App Auto-Start**: Adds an **All Users Startup** shortcut (runs after user logs in)
- **Silent Installation**: Supports unattended installation

## Prerequisites

1. **WiX Toolset** (v3.x)
   - Install via Chocolatey: `choco install wixtoolset`

2. **.NET 8 SDK**

## Build the Installer

From `adherence-monitoring-system/desktop-agent/installer`:

```powershell
.\build-installer.ps1
```

**Output:**
- `dist/AdherenceAgent.msi`

## Install / Uninstall

```powershell
# Install
msiexec /i "dist\AdherenceAgent.msi"

# Install silently
msiexec /i "dist\AdherenceAgent.msi" /quiet /norestart

# Uninstall
msiexec /x "dist\AdherenceAgent.msi"
```

## Notes

- The tray app auto-starts **on the next user logon** (Startup shortcut). If you install while already logged in, log off/on (or reboot) to see the tray.
