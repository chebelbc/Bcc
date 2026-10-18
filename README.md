# XProtect Admin Assistant

A Windows desktop application for Milestone XProtect administrators, integrators and support engineers. It makes
[MilestonePSTools](https://github.com/milestonesys/MilestonePSTools) usable through guided screens and wizards, so
nobody has to remember or type PowerShell. MilestonePSTools remains the automation engine underneath. The
application is **not** a PowerShell terminal: the user picks objects, fills in typed forms and reviews changes. The
application runs the corresponding MilestonePSTools commands in an isolated worker process.

> **Status: early version (0.1.0), not yet validated against a real XProtect system.** The worker engine, protocol
> and workflows are covered by automated tests against a mock of MilestonePSTools. The WPF application builds on
> Windows in CI, but it has not yet been run interactively or used with the real module. See [Status and known gaps](#status-and-known-gaps).

This is an independent project. It is not affiliated with or supported by Milestone Systems.

## Features

**Overview**
* **Dashboard:** connected system, management server, XProtect version, MilestonePSTools version, signed-in account,
  counts of recording servers, hardware and cameras (including disabled), warnings and favorites. Only values read
  from the system are shown.
* **Connect:** current Windows user, Windows account or basic user; connection profiles. Passwords are optional and
  stored in Windows Credential Manager.
* **Setup wizard:** checks Windows PowerShell 5.1 (x64), .NET, language mode, execution policy and the
  installed MilestonePSTools versions. Explains the MIP SDK EULA and telemetry. **Changes nothing silently.**

**Configuration**
* **System, Recording Servers, Hardware, Cameras & Devices, Storage, Users & Roles, Rules & Events, Smart Client:**
  searchable, filterable inventory built from structured data. Object pickers, never GUIDs.
* **Add Hardware wizard:** scan (express, address list or ranges, known driver), select results, add, then configure
  channels, names, storage and an optional device group. Each step is reviewed before it is applied.
* **Bulk onboarding from CSV or Excel (.xlsx)** in the documented `Import-VmsHardware` column format, including the
  `DeviceGroups` column. Files are validated offline and against the connected site ("120 rows detected, 116 valid,
  4 invalid"), and invalid rows can be inspected before anything runs. Excel and CSV templates can be downloaded, and
  the rows to fix can be exported as Excel or CSV.
* **MilestonePSTools workbooks:** an `.xlsx` exported by `Export-VmsHardware` is detected and imported by
  MilestonePSTools itself (`Import-VmsHardware`), with a preview, an optional "update existing" and strong confirmation.
* **Bulk camera changes:** enable/disable, rename (pattern or CSV), recording settings, storage.
* **Hardware:** enable/disable, edit (name, description, address), update device credentials (optionally also on
  the device), remove, reveal password (Expert Mode, audited).
* **Storage:** add a recording storage, remove storages that are not the default and not in use.
* **Users & Roles:** create roles, add and remove role members, create basic users, enable or lock them out, reset
  passwords, remove them.
* **Rules & Events:** enable, disable and rename rules. **Smart Client:** create, rename and remove view groups.
* **Sites:** switch between the sites of a Milestone Federated Architecture (System page).

**Operations**
* **Maintenance:** live device status and environment and recorder reachability checks.
* **Reports:** camera, hardware and recording server inventories, disabled cameras, device status, storage, roles,
  a configuration summary, and MilestonePSTools' own `Get-VmsCameraReport`. All come from read-only operations and
  export to CSV or JSON.
* **Automation (Job Builder):** deterministic, data-only jobs (source → filters → one action) that reuse the
  review and apply flow. The recording server or hardware, filter values and the new value are picked from lists
  built from the inventory.

**Expert**
* **Command Explorer:** all MilestonePSTools commands with documentation, risk, requirements and examples. Typed
  forms are generated for suitable commands (Expert Mode). Object parameters and GUID parameters whose object type is
  known (for example `-RecordingServerId`, `-CameraId`, `-RoleId`, or `-Id` of `Get-VmsCamera`) are shown as searchable
  lists of the objects in the system, so IDs never have to be typed. The lists come from the inventory or from the
  type's read-only `Get-` command (see `ObjectLookupService`).
* **Generated PowerShell** for every reviewed change: copy, or export as `.ps1`. Passwords never appear; they
  become `Read-Host`/`Get-Credential` prompts.
* **Activity log** (live, sanitized) and **audit log** (hash-chained, verifiable, exportable).

**Throughout:** review before apply, with *validated preview* and real *PowerShell -WhatIf* clearly distinguished;
strong confirmation for destructive work; per-object bulk results with retry and export of failures; plain-language
errors with expandable technical details; global search; favorites; features disabled with an explanation when the
installed module or the XProtect version does not support them.

## Requirements

* 64-bit Windows with Windows PowerShell 5.1 and .NET Framework 4.8.
* MilestonePSTools, installed separately (`Install-Module MilestonePSTools -Scope CurrentUser`). Validated with **25.2.61**.
* Network access from the workstation to the management server (HTTP/HTTPS) and, for status features, to the
  recording servers (default TCP 7563).

Details, the capability matrix and firewall notes: [COMPATIBILITY.md](COMPATIBILITY.md).

## Installation

1. Download the `XProtectAdminAssistant-<version>` artifact from a CI run of this repository
   (*Actions* → latest run → *Artifacts*), or build it yourself (see [DEVELOPMENT.md](DEVELOPMENT.md)). It contains:
   * `XProtectAdminAssistant-Setup-<version>.exe`, an installer. It installs for the current user by default (no
     administrator rights needed) and offers "all users" in its first dialog. It checks for .NET Framework 4.8 and
     creates a Start menu shortcut. Uninstalling keeps your settings, audit log and saved passwords.
   * `XProtectAdminAssistant-<version>-portable.zip`, the same files without an installer. Unzip the whole folder and
     keep `worker\` and `catalog\` next to `XProtectAdminAssistant.exe`.
   The files are not code-signed; Windows SmartScreen may warn about an unknown publisher.
2. Run the installer, or unzip the portable version.
3. If MilestonePSTools is not installed yet, install it from Windows PowerShell 5.1:
   `Install-Module MilestonePSTools -Scope CurrentUser`. The setup wizard can open a visible PowerShell window that
   runs this command after you confirm. PowerShell may then ask about the NuGet provider and the untrusted PSGallery
   repository; you answer those prompts yourself. The application never installs modules or changes PowerShell
   settings in the background.
4. Start `XProtectAdminAssistant.exe` and follow the setup wizard. Run it as a normal user; the application never asks
   for elevation.

## Documentation

| Document | Contents |
|---|---|
| [docs/INVESTIGATION.md](docs/INVESTIGATION.md) | What was verified about MilestonePSTools, with sources and open issues |
| [docs/COMMAND_INVENTORY.md](docs/COMMAND_INVENTORY.md) | Generated inventory of all 292 commands: verb groups, risk, categories, dependencies, GUI suitability |
| [SUPPORTED_COMMANDS.md](SUPPORTED_COMMANDS.md) | Command × PSTools version × GUI support × wizard × risk matrix (generated) |
| [ARCHITECTURE.md](ARCHITECTURE.md) | Processes, layers, worker protocol, execution engine, flows, UI and navigation design |
| [docs/adr/0001-process-architecture.md](docs/adr/0001-process-architecture.md) | Decision: WPF GUI plus isolated Windows PowerShell 5.1 worker |
| [SECURITY.md](SECURITY.md) | Credentials, injection protection, safeguards, audit log |
| [COMPATIBILITY.md](COMPATIBILITY.md) | Requirements, version model, capability matrix, remote deployment and ports |
| [DEVELOPMENT.md](DEVELOPMENT.md) | Building, repository layout, updating the catalog, adding features |
| [TESTING.md](TESTING.md) | Test strategy, suites, mock module rules, manual test plan |
| [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) | Licensing of MilestonePSTools, the MIP SDK and build/test packages |

## Status and known gaps

The request defined four MVP phases. Their current state:

| Phase | Scope | State |
|---|---|---|
| 1 | Environment checker, connection wizard and profiles, dashboard, dynamic command discovery, recording server, hardware and camera inventory, basic camera properties, Command Explorer, generated PowerShell, audit log, error handling | Implemented |
| 2 | Add hardware, enable/disable cameras, rename devices, hardware configuration, camera configuration, CSV onboarding | Implemented, plus Excel onboarding, device groups and MilestonePSTools workbook import |
| 3 | Storage, users/roles, rules/events, reports, Smart Client | Implemented as listed under Features: inventories, reports, and guided changes for storage, roles and members, basic users, rules and view groups |
| 4 | Job Builder | Implemented (camera source, filters, one action) |

Known gaps and limitations:

* **Not yet run against real MilestonePSTools or XProtect**, and the WPF UI has not been run interactively on Windows.
  The first Windows run should follow the manual plan in [TESTING.md](TESTING.md), using a test system only.
* **Excel files** are read by the application's own reader. It is tested with files written by openpyxl, XlsxWriter
  and LibreOffice, but not yet with files saved by Microsoft Excel itself.
* **No configuration backup/restore**, because MilestonePSTools has no such command.
* **Not covered by guided actions:** role permissions (configure them in Management Client; MilestonePSTools role
  permission commands are in Command Explorer), alarm definitions (`Set-VmsAlarmDefinition` has no Enabled
  parameter), Smart Client profiles and views, archive storages and storage retention changes. Commands for these are
  available through Command Explorer forms in Expert Mode, where suitable.
* **The installer and executables are not code-signed.**

## License

Apache License 2.0; see [LICENSE](LICENSE). MilestonePSTools is also Apache 2.0, and is installed separately, not
redistributed. Using it requires accepting the Milestone MIP SDK EULA; see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
