# Desktop Agent Packaging (Scripted Installer)

This folder provides a simple packaging flow (no CI, no MSI build tooling required) to produce a deployable bundle and helper scripts to install/uninstall the Windows Service and the Tray app.

## Prerequisites
- Windows with PowerShell 5+.
- .NET 8 SDK installed.
- Run PowerShell as Administrator when installing/uninstalling the service.

## Build the package
From the repository root:
```powershell
cd adherence-monitoring-system/desktop-agent
pwsh -File installer/build-package.ps1
```
Outputs:
- `dist/AdherenceAgent/` (service + tray binaries, configs)
- `dist/AdherenceAgent.zip` (compressed bundle)
- Helper scripts inside `dist/AdherenceAgent/`:
  - `install-service.ps1`
  - `uninstall-service.ps1`

## Install the service and tray
1) Unzip `dist/AdherenceAgent.zip` on the target machine (e.g., `C:\Program Files\AdherenceAgent`).
2) In an elevated PowerShell window:
```powershell
cd "C:\Program Files\AdherenceAgent"
.\install-service.ps1
```
This will:
- Create `%ProgramData%\AdherenceAgent` folders for logs/db.
- Register and start the Windows Service `AdherenceAgentService`.
- Optionally launch the tray app (toggle in the script).

## Uninstall
In an elevated PowerShell window from the install folder:
```powershell
.\uninstall-service.ps1
```
This stops and deletes the service. It does not delete `%ProgramData%\AdherenceAgent` data/logs.

## Notes
- Service name: `AdherenceAgentService`
- Default endpoint and settings are taken from `appsettings.json`; override via environment variables or edit the deployed `appsettings.json`.
- This is a lightweight installer script approach (agreed for initial deployment). A MSI/WiX-based installer can be added later when CI is available.

