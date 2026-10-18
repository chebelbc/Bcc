# Architecture

XProtect Admin Assistant is a Windows desktop application for XProtect administrators. It uses
[MilestonePSTools](https://github.com/milestonesys/MilestonePSTools) as its automation engine. It is **not** a
PowerShell terminal: the user works with screens, wizards and object pickers. The application turns those inputs into
typed MilestonePSTools invocations that run in an isolated PowerShell worker process.

* Why the process split: [docs/adr/0001-process-architecture.md](docs/adr/0001-process-architecture.md)
* What was verified about MilestonePSTools: [docs/INVESTIGATION.md](docs/INVESTIGATION.md)
* Security model: [SECURITY.md](SECURITY.md)

## Processes

```
┌──────────────────────────── XProtectAdminAssistant.exe (WPF, .NET Framework 4.8) ───────────────────────────┐
│  UI (views, view models)                                                                                  │
│    └── Application (workflows, review/apply, bulk runner, audit, activity, reports, jobs, Command Explorer) │
│          ├── Domain (catalog, classification, capabilities, change plans, CSV, script generation)          │
│          └── PSTools (command builders, DTO mapping, WorkerProcessExecutor) ── JSON lines over stdin/stdout ─┐  │
│    Infrastructure (settings/profile/job stores, Windows Credential Manager, audit log, catalog cache)       │  │
└────────────────────────────────────────────────────────────────────────────────────────────────────────────┼──┘
                                                                                                             │
┌──────────────────────── worker\MilestoneAdmin.Worker.exe (.NET Framework 4.8, x64) ─────────────────────────▼──┐
│  Worker.Engine: request validation, command policy, parameter binding, connection guard, custom PSHost,       │
│                 object serialization, handle table, secret scrubbing, cancellation                             │
│  Windows PowerShell 5.1 runspace (System.Management.Automation from the GAC)                                   │
│    └── MilestonePSTools (installed separately by the user) ── MIP SDK ── XProtect management/recording servers │
└────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

The GUI never loads `System.Management.Automation`, MilestonePSTools or the MIP SDK. The application starts one worker
per session and replaces it on reconnect, after a crash, or when an operation must be killed.

## Projects

| Project | Target | Responsibility |
|---|---|---|
| `MilestoneAdmin.Protocol` | netstandard2.0 | Dependency-free JSON reader/writer, message types, `PipelineStep`, typed `ParameterValue`s, `ErrorInfo`. Shared by both processes. |
| `MilestoneAdmin.Worker.Engine` | net48 (production), net8.0 (tests only) | The PowerShell host: everything that touches `System.Management.Automation`. |
| `MilestoneAdmin.Worker` | net48 x64 exe (production), net8.0 (tests) | Thin `Main`: reads requests from stdin, writes events to stdout. |
| `MilestoneAdmin.Domain` | netstandard2.0 | Command catalog and classifier, capabilities and version rules, change plans and diffs, CSV and .xlsx reading/writing and hardware file validation, PowerShell script generation for Expert Mode, audit record. |
| `MilestoneAdmin.PSTools` | netstandard2.0 | The only GUI-side code that knows MilestonePSTools command names (`Commands/PsCommands.cs`), DTO mapping, `IPowerShellExecutor` and the worker client. |
| `MilestoneAdmin.Application` | netstandard2.0 | Use cases: connection, environment checks, inventory, workflows (Add Hardware, CSV/Excel onboarding, MilestonePSTools workbook import, camera edits, hardware enable/disable/remove), quick actions, site selection, the review → WhatIf → apply runner, bulk operations, reports, Job Builder, Command Explorer forms, error translation, global search. |
| `MilestoneAdmin.Infrastructure` | netstandard2.0 | File-based stores (settings, profiles, jobs, catalog cache), Windows Credential Manager, the hash-chained JSONL audit log, diagnostics log, worker factory. |
| `MilestoneAdmin.UI` | net48 WPF exe | Shell, navigation, pages, wizards, dialogs; hand-written MVVM (`ObservableObject`, `RelayCommand`, `AsyncCommand`). |
| `tests/MilestoneAdmin.Tests` | net8.0 xUnit | Engine, protocol, end-to-end (real worker process), domain, application and drift tests against a mock module. |
| `tools/catalog`, `tools/discovery`, `tools/verify` | Python / PowerShell | Static and runtime catalog generation, documentation generation, UI verification on non-Windows. |

Dependencies point inward: UI → Application → Domain/PSTools → Protocol. Infrastructure implements Application interfaces.

## Worker protocol

Each message is one UTF-8 JSON document per line on the worker's redirected stdin/stdout: anonymous pipes owned by
the GUI, so nothing listens on the network or on a named pipe. Protocol version `1`.

| Direction | Type | Purpose |
|---|---|---|
| GUI → worker | `initialize` | Import MilestonePSTools (optionally a specific manifest path); set the telemetry environment variable first if the user chose that. |
| | `environment` | Report PowerShell/.NET/OS/elevation/language mode/execution policy/installed module versions without importing anything. |
| | `discover` | Build the runtime command catalog with `Get-Command`/`Get-Help` and attribute reflection. |
| | `invoke` | Run a pipeline of `{command, parameters}` steps with typed parameter values, a `secrets` map and options (`allowStateChange`, `whatIf`, `verbose`, timeout, output options). |
| | `cancel` | Stop the running pipeline. The GUI kills the worker if it does not stop in time. |
| | `releaseHandles` | Drop object handles the GUI no longer needs. |
| | `shutdown` | Disconnect and exit. |
| worker → GUI | `ready`, `progress`, `stream` (verbose/warning/information/whatif), `errorRecord`, `result`, `failed`, `log` | Lifecycle, progress, streams, structured results and errors. |

Parameter values are typed (`literal`, `switch`, `list`, `table`, `secret`, `credential`, `handle`, `reference`). The
engine builds every pipeline with `PowerShell.AddCommand` / `AddParameter`. **`AddScript` is never used, and no script
text crosses the boundary.**

### Execution engine (worker)

For each `invoke` the engine:

1. **Resolves each command** against the allowlist of commands exported by the loaded MilestonePSTools module, by
   module-qualified name. Names containing script syntax are rejected.
2. **Applies the policy.** Engine-controlled common parameters (`WhatIf`, `Confirm`, `ErrorAction`, ...) are refused
   from the GUI. `ScriptBlock`-typed parameters and interactive parameters (`-ShowDialog`) are refused. Mutating verbs
   require `allowStateChange`. Sensitive commands and parameters (for example `Get-VmsHardwarePassword`,
   `Get-VmsCameraReport -IncludePlainTextPasswords`) require explicit sensitive-output permission.
3. **Runs the connection guard.** If a command requires a VMS connection and `Test-VmsConnection` is false, the
   request fails. This stops MilestonePSTools from auto-connecting with a saved profile or a login dialog.
4. **Binds parameters** to the target parameter types: `secret` becomes `SecureString`, `credential` becomes
   `PSCredential`, and `handle`/`reference` become live objects.
5. **Runs the pipeline** with `$ConfirmPreference = 'None'` for approved state changes (confirmation already happened
   in the GUI) and `-WhatIf` for previews. Execution has a timeout and supports cancellation.
6. **Captures streams through a custom non-interactive `PSHost`.** "What if:" lines from ShouldProcess are captured
   as their own stream. Any prompt (`Read-Host`, credential prompt, choice) fails fast instead of hanging.
7. **Serializes results** from named properties into DTO JSON, never from formatted console text. Each object is
   registered in a **handle table**, and its type and Id are returned so it can be re-resolved later.
8. **Scrubs** every outgoing string (messages, errors, warnings, results) of the request's secret values.

**Handles and re-resolution.** Objects returned to the GUI carry a worker handle *and* their type plus Id. When a
handle is gone (worker restarted, handle released), a `reference` value is re-resolved through the type's producer,
for example `Get-VmsCamera -Id`. The producers are listed in `ReferenceResolver.Producers` and checked against the
catalog by tests. Names are never used to identify objects, because MilestonePSTools resolves strings by name and
fails on duplicates.

## Command catalog, classification and capabilities

* `catalog/milestonepstools-catalog.json` is the static catalog of all 292 commands. It is extracted from the official
  repository and shipped as a fallback.
* On connect, the worker builds the **runtime catalog** of the installed module (`discover`). It is cached per module
  version and path.
* `catalog/classification-rules.json` is shared by the Python tooling and the C# `CommandClassifier`, and a parity
  test keeps them in sync. It assigns:
  * a **risk level**: ReadOnly, Sensitive, Session, LocalState, Operational, Create, Modify, HighImpact, Delete;
  * a **category**;
  * **GUI suitability**: AutoForm, CustomWorkflow, ExpertOnly, AppManaged, NotSuitable;
  * a **control kind** for every parameter.
* `CapabilityCatalog` maps each GUI feature to the commands it needs. `CapabilityEvaluator` checks those commands
  against the installed catalog, the connected XProtect version (`RequiresVmsVersion`) and licensed features. Missing
  capabilities are **disabled with an explanation** instead of failing at run time. Configuration backup is listed as
  *not supported by MilestonePSTools*.

## Application flows

### Review → (WhatIf) → Apply

Every change goes through a `ChangePlan`. The plan holds per-object property diffs (current value from the VMS →
new value), the risk level, warnings, the generated PowerShell (Expert Mode) and whether WhatIf is available.

1. **Validated preview.** The application computes the plan from current values and validation rules. The UI labels
   it *Validated preview*.
2. **PowerShell WhatIf**, optional, only when the command supports ShouldProcess. The worker runs the same
   invocation with `-WhatIf` and returns the captured "What if:" lines. The UI labels them *PowerShell -WhatIf output*
   and keeps them separate from the validated preview.
3. **Confirmation.** Ordinary changes need one confirmation. Destructive or large operations
   (`RequiresStrongConfirmation`: deletes, bulk disables) need the user to type the object name or a count.
4. **Apply.** `BulkOperationRunner` runs one operation per object and reports progress and per-object results. It
   supports cancel (between objects; the worker is killed if needed), retry of failed objects, and export of failed
   objects to CSV.
5. **Audit.** Every state-changing or sensitive operation is written to the audit log.

### Wizards and pages

* **Add Hardware** groups the steps of the requested workflow into six screens:

  | Screen | Requested steps covered |
  |---|---|
  | 1. Recording server and discovery | Shows the selected site (switched on the System page); select recording server; enter IP addresses, host names or ranges (or an express scan); HTTP/HTTPS |
  | 2. Device credentials and driver | Provide credentials securely; select a driver or let the scan identify it |
  | 3. Select devices | Discover devices (`Start-VmsHardwareScan`); choose which results to add; optional hardware names |
  | 4. Review and add | Review planned changes; apply (`Add-VmsHardware`) with per-device results |
  | 5. Configure cameras | Select channels; configure names, storage and an optional device group; reviewed again before `Set-VmsCamera` / `Set-VmsDeviceStorage` / `New-VmsDeviceGroup` + `Add-VmsDeviceGroupMember` |
  | 6. Done | Success/failure summary; inventory refreshed |

  Pre-checks can be run on any screen. They cover: worker running, authenticated session, required commands present in
  the installed module, XProtect version requirements, licensed-feature and elevation requirements (warnings), user
  permissions, and TCP reachability of the recording server's web server from this workstation. Address syntax and
  duplicates are validated as the user types.
* **Sites.** In a Milestone Federated Architecture, MilestonePSTools commands work on the selected site. The System page
  lists the sites (`Get-VmsSite -ListAvailable`) and switches with `Select-VmsSite` after a confirmation. The session
  then re-reads the management server and drops the cached inventory, which is reloaded from the new site.
* **Bulk onboarding** reads CSV files (`CsvParser`) and Excel workbooks (`XlsxReader`: the first worksheet with an
  Address column) in the documented `Import-VmsHardware` column set. Validation runs offline and online (recording
  servers, installed drivers, storage names, device group paths, duplicate and existing addresses). It shows *"N rows
  detected, X valid, Y invalid"* before anything runs. Each hardware unit is then added, its cameras configured, and
  the `DeviceGroups` paths applied (`New-VmsDeviceGroup -Path`, which creates missing levels, piped to
  `Add-VmsDeviceGroupMember -DeviceId`).
* **MilestonePSTools workbooks.** A workbook with *Hardware* and *Cameras* worksheets (the layout written by
  `Export-VmsHardware`) is not read by the application. It is handed to `Import-VmsHardware -LiteralPath`, which applies
  all of its worksheets. The application previews the Hardware worksheet, flags rows MilestonePSTools will skip
  (existing hardware without `-UpdateExisting`, addresses that are not absolute URIs) and requires strong confirmation
  for updates. `Import-VmsHardware` has no `-WhatIf`, so no dry run is offered.
* **Inventory pages** (Dashboard, System, Recording Servers, Hardware, Cameras & Devices, Storage, Users & Roles,
  Rules & Events, Smart Client, Maintenance, Reports) list objects with filters and multi-select. Changes start from
  the selected rows (dedicated workflows or quick actions).

### Quick actions

Guided changes that each map to one documented command are defined as data in `QuickActionCatalog` (Application). Each
action names:
* the command and, where needed, its parameter set;
* the parameter that receives the selected objects (or one of their properties, for example member SIDs);
* the few command parameters to show as inputs, with optional prefill from the selected object;
* fixed values (for example `-Enabled $false` for *Disable rules*), and whether strong confirmation is required.

`QuickActionService` builds the inputs with `CommandFormBuilder` from the **installed module's** catalog, so types,
accepted values and secret handling come from MilestonePSTools metadata. An action is unavailable, with a reason, when
the installed version lacks the command or a parameter. Prefilled values the user did not change are not sent.
Objects already in the requested state are skipped. The plan goes through the normal review → *PowerShell -WhatIf*
(only when the command supports ShouldProcess) → apply → audit flow. `CatalogDriftTests` checks every action against
the catalog extracted from the MilestonePSTools repository.

Actions currently defined:
* **Hardware:** edit (name, description, address); update device credentials.
* **Storage:** add a recording storage; remove storages.
* **Roles:** new role; add members; remove members.
* **Basic users:** new; enable; lock out; reset password; remove.
* **Rules:** enable; disable; rename.
* **View groups:** new; rename; remove.
* **Command Explorer** lists every catalog command with documentation, risk, requirements and examples. In Expert Mode,
  AutoForm commands get a generated typed form (`CommandFormBuilder`): object pickers instead of GUIDs, dropdowns for
  accepted values, secure inputs for secrets. Forms become typed operations, never script text.
* **Job Builder** creates deterministic, data-only jobs: a camera source, filters, then one action (set one camera
  property, or export an inventory report). Jobs reuse the same review and apply flow. They never contain code.
* **Activity Log** shows a live, sanitized activity stream (not a console). **Audit Log** shows the hash-chained audit
  history, verifies the chain, and exports it.
* **Setup wizard** checks the environment (Windows PowerShell 5.1, x64, .NET, language mode, module presence and
  versions, execution policy) and explains the MIP SDK EULA and telemetry. It shows install commands for the user to run
  but **changes nothing silently** (see SECURITY.md).

### Guided vs Expert mode

Guided mode shows wizards and pages only. Expert Mode adds:
* generated PowerShell for every reviewed change (copy, or export `.ps1`), with passwords replaced by `Read-Host`/`Get-Credential` prompts;
* running AutoForm commands from Command Explorer;
* revealing hardware passwords, which requires a strong confirmation and is audited.

## Errors

`ErrorTranslator` maps exception types (including the MilestonePSTools exceptions listed in INVESTIGATION.md), error
ids and categories to an `AdminError`: a plain-language summary, possible causes, suggested checks, and the full
PowerShell error (expandable *technical details*, copyable). Errors are never parsed from console text.

## Data on disk

| Data | Location | Notes |
|---|---|---|
| Settings, connection profiles, jobs | `%APPDATA%\XProtectAdminAssistant\*.json` | No secrets by design. |
| Saved passwords | Windows Credential Manager (generic credentials) | DPAPI-protected per Windows user; never in files. |
| Audit log | `%LOCALAPPDATA%\XProtectAdminAssistant\audit\audit.jsonl` | Append-only JSON Lines with a SHA-256 hash chain. |
| Catalog cache | `%LOCALAPPDATA%\XProtectAdminAssistant\catalog-cache\` | Per module version and path. |
| Diagnostics | `%LOCALAPPDATA%\XProtectAdminAssistant\logs\` | Sanitized worker diagnostics. |

## Testing seams

The engine multi-targets net8.0, so the real `WorkerEngine` runs in a PowerShell 7 SDK runspace against
`tests/MockModules/MilestonePSTools`. That is a test double whose command and parameter names are checked against the
real catalog. End-to-end tests start the real worker process and drive it through `WorkerProcessExecutor` and the
application services. See [TESTING.md](TESTING.md).
