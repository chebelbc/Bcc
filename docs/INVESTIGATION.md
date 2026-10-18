# MilestonePSTools Investigation

This records what was verified about MilestonePSTools before and during the design of XProtect Admin Assistant, and
where each fact comes from. The command-level inventory is generated separately:

* [COMMAND_INVENTORY.md](COMMAND_INVENTORY.md): totals, verb groups, risk levels, categories, dependencies and GUI suitability for all 292 commands.
* [../SUPPORTED_COMMANDS.md](../SUPPORTED_COMMANDS.md): which commands the GUI uses, and where.
* [adr/0001-process-architecture.md](adr/0001-process-architecture.md): the architecture decision that follows from these facts.

## Source and method

| Item | Value |
|---|---|
| Repository | https://github.com/milestonesys/MilestonePSTools (official, Milestone Systems) |
| Commit inspected | `a9701bec0a842546c84c3dab29f51eb0a91f1ba5` (2026-10-09) |
| Module version | 25.2.61, the latest release in `docs/changelog.md` (released 2026-05-14); `version.json` = 25.2 |
| License | Apache License 2.0 (`LICENSE`) |
| Inspection date | 2026-10-18 |

Method: the catalog was extracted **statically** from the repository by `tools/catalog/extract_repo_catalog.py`. It
merges the platyPS reference documentation (`docs/commands/en-US/*.md`), the PowerShell function sources
(`MilestonePSTools/Public/**/*.ps1`), the C# cmdlet sources (`src/MilestonePSTools/**/*.cs`) and
`MilestonePSTools/deprecations.psd1`. No PowerShell was executed: the module needs Windows PowerShell 5.1 on Windows, and
this investigation ran on Linux.

The **runtime** discovery script `tools/discovery/Export-PSToolsCatalog.ps1` produces the same schema from
`Get-Command`/`Get-Help` and attribute reflection on a machine with the module installed. When it is available, the
application prefers the runtime catalog (it discovers it automatically on connect). The two methods agree on the mock
module used in tests; they have **not** yet been compared against a real installed 25.2.61 module (see open issues).

> MilestonePSTools is actively developed. Everything below describes commit `a9701bec`. Re-run both catalog tools when
> upgrading, and treat anything here as possibly outdated for later versions.

## Findings

### Platform and loading

| # | Finding | Evidence |
|---|---|---|
| F1 | Requires **Windows PowerShell 5.1** Desktop edition, **x64**, .NET Framework 4.7+ (the README says 4.7.2) and FullLanguage mode. It does not run on PowerShell 7. | `MilestonePSTools.psd1`: `CompatiblePSEditions = 'Desktop'`, `PowerShellVersion = '5.1'`, `DotNetFrameworkVersion = '4.7'`, `ProcessorArchitecture = 'Amd64'`; README "Prerequisites" |
| F2 | Loads the MIP SDK (`VideoOS.Platform*` 25.2.x) and third-party assemblies into the host process. | `src/MilestonePSTools/MilestonePSTools.csproj`, manifest `RequiredAssemblies` |
| F3 | Connection state is process-global (`MilestoneConnection.Instance`, static MIP SDK environment). | `src/MilestonePSTools/Connection/MilestoneConnection.cs` |
| F4 | Some code paths change process-wide state, such as `[Environment]::CurrentDirectory`. | `MilestonePSTools.psm1` (argument completer for `Start-Export`) |
| F5 | Media commands use native MIP SDK media components inside the host process. | `SnapshotCommands/*`, `ExportCommands/StartExport.cs` |
| F6 | The README lists supported XProtect products as "XProtect Essential+, Express+, Professional+, Expert, or Corporate 2014 or newer". Individual commands narrow this with `[RequiresVmsVersion]` (72 commands). | README "Prerequisites"; catalog `requirements.minVmsVersion` |

### Command metadata the GUI relies on

| # | Finding | Evidence |
|---|---|---|
| F7 | Requirements are declared with attributes: `RequiresVmsConnection`, `RequiresVmsVersion`, `RequiresVmsFeature`, `RequiresElevation`, `RequiresInteractiveSession`, `RequiresVmsWindowsUser`. Failures raise `VmsNotConnectedException`, `VmsVersionValidationException`, `VmsFeatureNotAvailableException`, `AdminElevationRequiredException` and `InteractiveSessionRequiredException`. | `src/MilestonePSTools/Attributes.cs` |
| F8 | Commands with `[RequiresVmsConnection()]` **try to connect automatically** when no session exists, using the saved "default" connection profile or an interactive login dialog. | `Attributes.cs`, `Connect-Vms.ps1` |
| F9 | `-WhatIf` output goes to the PowerShell **host UI** ("What if: ..."), not to an output stream. 80 commands support ShouldProcess; 20 declare `ConfirmImpact = High`. | PowerShell ShouldProcess semantics; catalog |
| F10 | `MipItemTransformation` turns *strings* into configuration items **by name** and fails on duplicate names. | `src/MilestonePSTools/Utility/MipItemNameArgumentCompleter.cs` and the transformation attribute |
| F11 | `Set-VmsCamera`, `Set-VmsMicrophone` and similar cmdlets derive from `SetDeviceCommandBase`. It sets the required item type from the cmdlet's `[OutputType]` and rejects other item types. | `src/MilestonePSTools/DeviceCommands/SetDeviceCommandBase.cs` |
| F12 | 12 commands are deprecated but still exported (`deprecations.psd1`). One exported command, `Set-NewHardwareConfig`, has no reference documentation. | `deprecations.psd1`; catalog `discrepancies` |

### Secrets, telemetry and licensing

| # | Finding | Evidence |
|---|---|---|
| F13 | Some commands return secrets: `Get-VmsHardwarePassword` (hardware password), `Get-VmsToken` (bearer token), `Get-VmsConnectionString` (SQL connection strings), and `Get-VmsCameraReport -IncludePlainTextPasswords`. | Command docs; `catalog/classification-rules.json` (`commandRiskOverrides`, `sensitiveParameters`) |
| F14 | `Export-VmsHardware` writes hardware user names and **plain-text passwords** into the exported CSV/XLSX. | `MilestonePSTools/Private/ExportHardwareCsv.ps1`, `ImportVmsHardwareExcelFunctions.ps1` |
| F15 | The module sends telemetry on import, connect and command use unless it is disabled. `$env:VMS_ApplicationInsights__Enabled = 'false'` must be set before import; `Set-VmsModuleConfig -EnableTelemetry $false` changes the persistent setting. | `docs/commands/en-US/about_Telemetry.md`, `MilestonePSTools.psm1` |
| F16 | `Connect-Vms -AcceptEula` accepts the Milestone MIP SDK end-user license agreement. The application passes it only after the user accepts it explicitly in the setup wizard or Settings. | `Connect-Vms` documentation |
| F17 | The module is installed from the PowerShell Gallery with `Install-Module MilestonePSTools` (all users, needs Administrator) or `-Scope CurrentUser`. | `docs/getting-started.md` |

### Gaps relevant to the requested features

| # | Finding | Evidence |
|---|---|---|
| G1 | There is **no configuration backup/restore command**. Object-level exports exist (`Export-VmsHardware`, `Export-VmsRole`, `Export-VmsRule`, `Export-VmsViewGroup`, `Export-VmsClientProfile`). A backup/restore feature request was open in the repository's issue tracker (#121) when checked on 2026-10-18. The GUI shows "backup" as *not supported by MilestonePSTools* instead of inventing one. | Catalog; GitHub issue #121 (status may have changed) |
| G2 | "Replace Hardware" cannot be performed through the MIP SDK or MilestonePSTools. | `docs/commands/en-US/about_Get-VmsCameraReport.help.md` |
| G3 | Device status and parts of `Get-VmsCameraReport` contact **recording servers directly**. The documented default port is 7563. | `about_Get-VmsCameraReport.help.md`, `Get-RecorderStatusService2.md`, `Get-RecorderConfig.md` |

### Commands behind the guided write features

Checked in the documentation and source at the same commit. The quick-action and onboarding code relies on these facts.

| # | Finding | Evidence |
|---|---|---|
| W1 | `Import-VmsHardware` handles `.csv` itself and `.xlsx` through `Import-VmsHardwareExcel`, using the ImportExcel module (an installed copy, or the copy shipped inside MilestonePSTools). The Excel format is "too complex to describe" and is meant to be produced by `Export-VmsHardware`. | `Public/Import-VmsHardware.ps1`, `Import-VmsHardware.md` |
| W2 | `Import-VmsHardwareExcel` requires a *Hardware* worksheet. It skips hardware that already exists on the recording server unless `-UpdateExisting` is given, and skips rows whose Address is not an absolute URI. `Import-VmsHardware` does not support `-WhatIf`. | `Private/ImportVmsHardwareExcelFunctions.ps1`; catalog |
| W3 | `New-VmsDeviceGroup -Path` returns an existing group or creates the missing levels (camera groups by default). `Add-VmsDeviceGroupMember` takes `-Group` from the pipeline and devices by `-DeviceId`. `Add-VmsHardware` does not add devices to device groups. | `Public/New-VmsDeviceGroup.ps1`, `Public/Add-VmsDeviceGroupMember.ps1`, `Add-VmsHardware.md` |
| W4 | `Set-VmsHardware -UpdateRemoteHardware` also changes the password on the device. It requires XProtect 23.2, driver support, and Milestone's password rules (up to 64 characters, at least one upper-case, one lower-case and one number, only a-z, A-Z, 0-9). `-Password` requires 11.3. | `Set-VmsHardware.md` |
| W5 | `Remove-VmsStorage` removes a storage only if it is not the default and no device records to it, together with its archive storages. | `Remove-VmsStorage.md` |
| W6 | `Remove-VmsViewGroup -Recurse` removes all child views and view groups. | `Remove-VmsViewGroup.md` |
| W7 | `Add-VmsRoleMember -AccountName` resolves names without a domain on the management server's machine first, then in Active Directory. `Remove-VmsRoleMember` accepts members as `-User` objects or as `-Sid`. | `Add-VmsRoleMember.md`, catalog |
| W8 | `Set-VmsRule` has `-Enabled` and `-Name`. `Set-VmsAlarmDefinition` has **no** Enabled parameter. | catalog |
| W9 | `Set-VmsBasicUser -Status` accepts `Enabled` and `LockedOutByAdmin`. | catalog (ValidateSet) |
| W10 | `[RequiresVmsVersion]` exposes `RequiredVersion` as a `VersionInterval` with `MinVersion` and `MaxVersion`. | `src/MilestonePSTools/Attributes.cs` |
| W11 | `Get-VmsSite` returns the selected site, and `-ListAvailable` returns all sites. Most commands operate only on the selected site; `Select-VmsSite -Site` switches it. | `Get-VmsSite.md`, `Select-VmsSite.md` |

## Consequences for the design

* F1–F5 lead to the separate worker process (ADR 0001).
* F7/F9 feed the capability model: features are disabled with a reason instead of failing at run time. Real WhatIf is
  shown only when the command supports ShouldProcess.
* F8 leads to a connection guard. The worker checks `Test-VmsConnection` before any command that requires a
  connection, so PSTools never opens a login dialog or connects with a saved profile behind the user's back.
* F10 means objects are always passed by **handle or Id**, never by name.
* F13–F15 lead to the sensitive-output gate, secret scrubbing, the audit trail for secret reveals, and telemetry
  disabled by default for the application's worker.
* G1 is shown in the UI as an explicit *not supported* capability.
* W1/W2 lead to two Excel paths. A simple table in the documented column format is read and validated by the
  application. A MilestonePSTools export workbook is passed to `Import-VmsHardware`, with the skip rules shown in the
  preview and no claimed dry run.
* W3 makes device-group assignment idempotent. W4–W9 supply the warnings and fixed values of the quick actions. W10 is
  mirrored by the mock module.

## Open issues and uncertainties

1. **Not yet verified against a real XProtect system or a real module installation.** All automated tests use a mock
   module (`tests/MockModules/MilestonePSTools`) whose command and parameter names are checked against the catalog.
   Before production use, run `Export-PSToolsCatalog.ps1` on a Windows workstation with 25.2.61 installed, compare it with
   the static catalog, and run the read-only workflows against a **test** system.
2. **The WPF application builds on Windows but has not been run interactively.** The CI job on `windows-latest`
   builds the whole solution with the real XAML compiler and checks the output layout (first green run: CI run #1 on
   this branch). Nobody has yet opened the UI and clicked through it, so rendering and interaction problems may remain.
3. **DTO property names** come from the module's own usage. For example, `RecordingFramerate` is used in
   `docs/commands/en-US/Set-VmsCamera.md` and `$cam.RecordingStorage` in `Public/Reports/Get-VmsCameraReport.ps1`. The
   worker reads properties by name and tolerates missing ones, but every mapping should still be checked on a real system.
4. **`Set-VmsDeviceStorage`** documentation does not say what happens to recordings already on the old storage. The GUI
   warns about this and links the user to the XProtect documentation instead of making a claim.
5. **Network ports** beyond those documented in MilestonePSTools (management server HTTP/HTTPS, recording server 7563)
   depend on the XProtect version and configuration. Use Milestone's official port documentation for your version.
6. **Excel files saved by Microsoft Excel** have not been tested (the reader is tested with openpyxl, XlsxWriter and
   LibreOffice output). Office Open XML is a standard format, but a file from Excel itself should be tried before relying on it.
7. **Runtime versus static catalog differences** (aliases, dynamic parameters, parameter sets generated at run time)
   can only be found by running the runtime discovery on Windows.
