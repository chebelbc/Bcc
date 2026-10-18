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
  channels, names and storage. Each step is reviewed before it is applied.
* **CSV onboarding** in the documented `Import-VmsHardware` format, validated offline and against the connected
  site ("120 rows detected, 116 valid, 4 invalid"), with invalid rows inspectable before anything runs.
* **Bulk camera changes:** enable/disable, rename (pattern or CSV), recording settings, storage. **Hardware:**
  enable/disable, remove, reveal password (Expert Mode, audited).

**Operations**
* **Maintenance:** live device status and environment and recorder reachability checks.
* **Reports:** camera, hardware and recording server inventories, disabled cameras, device status, storage, roles,
  a configuration summary, and MilestonePSTools' own `Get-VmsCameraReport`. All come from read-only operations and
  export to CSV or JSON.
* **Automation (Job Builder):** deterministic, data-only jobs (source → filters → one action) that reuse the
  review and apply flow.

**Expert**
* **Command Explorer:** all MilestonePSTools commands with documentation, risk, requirements and examples. Typed
  forms are generated for suitable commands (Expert Mode).
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

There is no installer yet.

1. Build on Windows (see [DEVELOPMENT.md](DEVELOPMENT.md)), or download the `XProtectAdminAssistant` artifact from a
   CI run of this repository.
2. Copy the whole output folder (it contains `XProtectAdminAssistant.exe`, `worker\` and `catalog\`) to the workstation,
   for example `C:\Program Files\XProtect Admin Assistant\`.
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
| 2 | Add hardware, enable/disable cameras, rename devices, hardware configuration, camera configuration, CSV onboarding | Implemented; hardware configuration is limited to enable/disable/remove |
| 3 | Storage, users/roles, rules/events, reports, Smart Client | Read-only inventories and reports implemented; camera storage changes implemented; no other write workflows yet |
| 4 | Job Builder | Implemented (camera source, filters, one action) |

Known gaps and limitations:

* **Not yet run against real MilestonePSTools or XProtect**, and the WPF UI has not been run on Windows. The first
  Windows run should follow the manual plan in [TESTING.md](TESTING.md), using a test system only.
* **No site switching** in a Milestone Federated Architecture (`Select-VmsSite`). Work happens on the site you
  connected to.
* **No hardware rename or other hardware settings** beyond enable/disable/remove. No device group assignment for new
  hardware (`Add-VmsHardware` does not do this; `Import-VmsHardware` in Expert Mode can).
* **No configuration backup/restore**, because MilestonePSTools has no such command.
* Write workflows for users/roles, rules, Smart Client and storage configuration are not implemented. Those commands
  are available through Command Explorer forms in Expert Mode, where suitable.

## License

Apache License 2.0; see [LICENSE](LICENSE). MilestonePSTools is also Apache 2.0, and is installed separately, not
redistributed. Using it requires accepting the Milestone MIP SDK EULA; see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
