# Compatibility

## Requirements

| Component | Requirement | Source |
|---|---|---|
| Operating system | 64-bit Windows with Windows PowerShell 5.1 and .NET Framework 4.8. Intended targets are current Windows 10/11 and Windows Server releases. **No OS version has been tested yet.** | Application build targets; MilestonePSTools manifest |
| .NET Framework | 4.8 (GUI and worker). MilestonePSTools itself declares 4.7 in its manifest; its README says 4.7.2. | `MilestonePSTools.psd1`, README |
| PowerShell | Windows PowerShell 5.1, Desktop edition, x64, FullLanguage mode. PowerShell 7 is not supported by MilestonePSTools. | `MilestonePSTools.psd1` (`CompatiblePSEditions = 'Desktop'`, `ProcessorArchitecture = 'Amd64'`) |
| Execution policy | Any policy except `Restricted`. `AllSigned` requires a trusted signature on every module script. The application never changes the policy. | Setup wizard checks |
| MilestonePSTools | Installed separately by the user (`Install-Module MilestonePSTools`, optionally `-Scope CurrentUser`). Validated against **25.2.61**. | `docs/getting-started.md` in the MilestonePSTools repository |
| XProtect | The MilestonePSTools README lists "XProtect Essential+, Express+, Professional+, Expert, or Corporate 2014 or newer". Individual commands require newer versions or licensed features; see the capability matrix. | README; command attributes |
| Privileges | A standard Windows user. XProtect permissions come from the XProtect role of the connected account. A few MilestonePSTools commands need local Administrator rights; the application flags them and never elevates. | `[RequiresElevation()]` (3 commands) |

> Version numbers and supported products may have changed after 25.2.61 (released 2026-05-14 per the repository
> changelog). Check the MilestonePSTools changelog and Milestone's product documentation for current information.

## Version model

* **MilestonePSTools version.** On connect, the worker discovers the commands actually exported by the installed
  module, including parameters, parameter sets and requirement attributes. A feature is enabled only when all of its
  commands exist. If the installed version differs from the validated version (25.2.61), the environment check
  (setup wizard and Maintenance) shows a warning. Command Explorer lists the commands of the installed version.
* **XProtect version.** MilestonePSTools expresses requirements as product versions (`[RequiresVmsVersion('21.1')]`;
  21.1 = XProtect 2021 R1). The application compares them with the connected management server's version
  (major.minor), and disables affected features with the required version in the explanation.
* **Licensed features.** `[RequiresVmsFeature]` requirements (28 commands) are shown as warnings. MilestonePSTools
  verifies the license itself when the command runs.

## Capability matrix

What each guided feature needs. *Min XProtect* is the highest `RequiresVmsVersion` among the feature's commands at
MilestonePSTools 25.2.61 ("-" = no version attribute).

| Feature | Commands | Min XProtect | Licensed feature |
|---|---|---|---|
| Connect to XProtect | `Connect-Vms`, `Disconnect-Vms`, `Test-VmsConnection`, `Get-VmsManagementServer` | - | - |
| Recording server inventory | `Get-VmsRecordingServer` | - | - |
| Hardware inventory | `Get-VmsHardware` | - | - |
| Add hardware (scan + add) | `Start-VmsHardwareScan`, `Add-VmsHardware`, `Get-VmsHardwareDriver` | - | - |
| Enable/disable hardware | `Set-VmsHardware` | - | - |
| Remove hardware | `Remove-VmsHardware` | - | - |
| CSV hardware onboarding | `Add-VmsHardware`, `Start-VmsHardwareScan`, `Get-VmsRecordingServer`, `Get-VmsHardwareDriver` | - | - |
| Camera inventory | `Get-VmsCamera` | - | - |
| Camera properties (enable, rename, recording) | `Set-VmsCamera` | - | - |
| Change camera recording storage | `Set-VmsDeviceStorage`, `Get-VmsStorage` | - | - |
| Live device status | `Get-VmsDeviceStatus` | - | - |
| Storage information | `Get-VmsStorage` | - | - |
| Role inventory | `Get-VmsRole` | - | - |
| Basic user inventory | `Get-VmsBasicUser` | - | - |
| Rule inventory | `Get-VmsRule` | 20.1 | - |
| Alarm definition inventory | `Get-VmsAlarmDefinition` | - | - |
| Smart Client views | `Get-VmsViewGroup` | 21.1 | - |
| Smart Client profiles | `Get-VmsClientProfile` | 21.2 | SmartClientProfiles |
| License overview | `Get-VmsLicenseOverview` | 20.2 | - |
| Detailed camera report | `Get-VmsCameraReport` | - | - |
| Configuration backup / restore | *none*: MilestonePSTools has no backup/restore command | n/a | n/a |

Every other command is listed in [SUPPORTED_COMMANDS.md](SUPPORTED_COMMANDS.md) with its GUI support level and risk.

## Remote workstation deployment

The application is meant to run on an administrator workstation, not necessarily on an XProtect server.

```
Admin workstation                                   XProtect
┌───────────────────────────────┐   HTTP 80 / HTTPS 443   ┌────────────────────┐
│ XProtectAdminAssistant.exe    │ ─────────────────────▶ │ Management server  │
│  └─ worker (PS 5.1 + PSTools) │                        └────────────────────┘
│                               │   TCP 7563 (default)   ┌────────────────────┐
│                               │ ─────────────────────▶ │ Recording servers  │
└───────────────────────────────┘                        └────────────────────┘
```

| Traffic | Port (default) | Needed for | Source |
|---|---|---|---|
| Workstation → management server | TCP 80 (HTTP) or 443 (HTTPS) | Login and all configuration commands | The address used with `Connect-Vms`; MilestonePSTools docs |
| Workstation → each recording server | TCP **7563** (recording server web server) | Live device status (`Get-VmsDeviceStatus`), the status columns of `Get-VmsCameraReport`, the recorder reachability pre-check | `about_Get-VmsCameraReport.help.md` ("Recording Server on port 7563 (default)"), `Get-RecorderStatusService2.md` |
| Workstation → devices | None from the workstation | Hardware scans and additions are performed by the recording server, which needs to reach the devices | MilestonePSTools `Start-VmsHardwareScan` / `Add-VmsHardware` operate through the recording server |

Notes and uncertainties:

* The table lists only ports **documented in the MilestonePSTools repository** for what this application does.
  Systems with customized ports, an external IdP, failover servers, mobile servers or a Federated Architecture use
  additional ports. **Check Milestone's official "ports used by the system" documentation for your XProtect version**
  before you write firewall rules.
* The recording server's web server address comes from its configuration (`WebServerUri`/`ActiveWebServerUri`). The
  reachability pre-check uses that address, so a non-default port is honoured.
* Basic users usually require HTTPS. Use the *Secure only* connection option to refuse plain HTTP.
* No inbound firewall rule is needed on the workstation. The worker communicates with the GUI over anonymous pipes
  and opens no listening sockets.

## Known gaps

* **Federated Architecture site switching** (`Select-VmsSite`) is not implemented. The application works on the site
  it connected to. The *Include child sites* connection option is passed to `Connect-Vms`, but there is no site picker yet.
* **Configuration backup/restore** does not exist in MilestonePSTools.
* **Device group assignment** of newly added hardware is not part of the guided workflows. `Add-VmsHardware` does not
  add devices to device groups (its documentation says so explicitly). `Import-VmsHardware`, available from Command
  Explorer in Expert Mode, supports a `DeviceGroups` column.
