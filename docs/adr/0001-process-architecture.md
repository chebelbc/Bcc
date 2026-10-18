# ADR 0001 — Process architecture: WPF GUI + isolated Windows PowerShell 5.1 worker

* Status: **Accepted**
* Date: 2026-10-18
* Deciders: project architecture (see repository history)

## Context

The application must drive MilestonePSTools, which is the automation engine. Facts established by inspecting the
official repository (commit `a9701bec`, module 25.2.61) — see [INVESTIGATION.md](../INVESTIGATION.md):

| # | Fact | Evidence |
|---|------|----------|
| F1 | Module requires **Windows PowerShell 5.1** (Desktop edition), **.NET Framework 4.7.2+**, **x64**, FullLanguage mode. It does not run on PowerShell 7. | `MilestonePSTools.psd1` (`CompatiblePSEditions = 'Desktop'`, `PowerShellVersion = '5.1'`, `ProcessorArchitecture = 'Amd64'`); README prerequisites; `docs/getting-started.md` |
| F2 | The module loads the MIP SDK (`VideoOS.Platform*.dll` 25.2.x) plus third-party assemblies (Azure.Identity 1.14.2, Microsoft.Extensions.Configuration 9.0.7, CsvHelper 33.1.0 and their transitive dependencies) into the host process. | `src/MilestonePSTools/MilestonePSTools.csproj`, `RequiredAssemblies` in the manifest |
| F3 | Connection state is **process-global**: a `public static MilestoneConnection Instance` field and the static MIP SDK environment (`VideoOS.Platform.SDK.Environment.AddServer/Login`). | `src/MilestonePSTools/Connection/MilestoneConnection.cs` lines 28, 208-209 |
| F4 | Some code paths mutate **process-wide state** such as `[Environment]::CurrentDirectory` (MIP SDK media exporter). | `MilestonePSTools.psm1` argument completer for `Start-Export` |
| F5 | Media features (snapshots, playback tests, exports) use MIP SDK media classes; faults in native media components can take down the hosting process (general property of in-process native code, not a specific known bug). | `SnapshotCommands/GetSnapshot.cs`, `GetVideoSource.cs`, `TestPlayback.cs`, `ExportCommands/StartExport.cs` |
| F6 | Some commands are interactive (login dialog, `Select-Camera`), `-WhatIf` output is written to the **host UI**, not to a stream. | `[RequiresInteractiveSession()]`; PowerShell ShouldProcess semantics |
| F7 | The module sends telemetry on import/connect/command use unless opted out via `$env:VMS_ApplicationInsights__Enabled=false` (before import) or `Set-VmsModuleConfig -EnableTelemetry $false`. | `docs/commands/en-US/about_Telemetry.md`, `MilestonePSTools.psm1` |
| F8 | `MipItemTransformation` resolves *strings* to objects by **Name** and fails on duplicates, so passing names is not a safe way to identify objects. | `src/MilestonePSTools/Utility/MipItemNameArgumentCompleter.cs` |

## Options considered

### Option A — WPF on .NET Framework 4.8 hosting Windows PowerShell 5.1 runspaces in-process

* \+ Same runtime as the module; live objects can be passed between calls; simplest MVP.
* − F2: the module's dependency graph is loaded into the GUI process. .NET Framework has a single binding context per
  AppDomain; any version skew between GUI dependencies and module dependencies produces `FileLoadException`s that
  cannot be fixed with binding redirects owned by the module.
* − F3: reconnecting to a different VMS, or recovering from a broken MIP SDK session, has no reliable reset short of
  restarting the process — the GUI itself.
* − F4/F5: process-wide side effects and native faults hit the GUI (file dialogs with wrong current directory, crash
  loses unsaved work and audit context).
* − Cancellation: blocking MIP SDK calls may ignore `PowerShell.Stop()`; the only guaranteed cancel is process
  termination, which is not acceptable for the GUI process.
* − Module upgrade requires restarting the GUI because module assemblies stay locked.

### Option B — Separate GUI process + Windows PowerShell 5.1 worker process, structured protocol

* \+ Isolation: crashes, hangs, process-wide state and dependency conflicts stay inside the worker; the GUI restarts it.
* \+ Guaranteed cancellation (kill the worker after a grace period) and clean reconnects (new worker per session).
* \+ The worker's own dependency footprint can be kept at **zero third-party assemblies**, so it cannot conflict with
  whatever MilestonePSTools ships.
* \+ A strict request/response contract is a natural PowerShell-injection boundary (no script text crosses it).
* \+ The GUI can be modernized (e.g. .NET 8 WPF) later without touching the worker.
* − Objects cannot be passed by reference across processes → mitigated with a **handle table** in the worker plus
  Id-based re-resolution (`Get-VmsCamera -Id`, …) when a handle is gone.
* − Serialization cost and a few seconds of worker start-up/module import → done once per session, asynchronously,
  with progress.

## Decision

**Option B, on the same runtime as Option A.**

* `MilestoneAdmin.UI` — WPF, **.NET Framework 4.8**. Never loads System.Management.Automation or the MIP SDK.
* `MilestoneAdmin.Worker` — **.NET Framework 4.8, x64** console executable hosting **Windows PowerShell 5.1**
  (`System.Management.Automation` from the GAC) in a runspace. One worker per connected VMS session.
* IPC — the worker's redirected **stdin/stdout** (anonymous pipes owned by the parent; nothing listens on the network
  or on a named pipe), one UTF-8 **JSON document per line**. Requests describe commands and typed parameters; the
  worker builds pipelines exclusively with `PowerShell.AddCommand/AddParameter`. `AddScript` is never used.
* The engine is a separate library (`MilestoneAdmin.Worker.Engine`) that also targets net8.0 so it is tested against a
  real PowerShell runspace (PowerShell SDK 7.4) with a mock module in CI on any OS; production uses only the net48 build.

Choosing .NET Framework 4.8 for the GUI is deliberate rather than conservative: both processes share
`netstandard2.0` libraries, need a single toolchain, and WPF on .NET Framework 4.8 is supported for the lifetime of
Windows. Because the boundary is a protocol, moving the GUI to .NET 8+ later is a contained change.

## Consequences

* All PowerShell knowledge lives in `MilestoneAdmin.PSTools` (GUI side: command builders, DTO mapping) and the worker.
* DTOs are produced by reading **named properties** from PowerShell objects in the worker; console formatting is never parsed.
* Objects returned to the GUI carry a worker handle **and** their type/Id so they can be re-resolved after a worker
  restart (supported for types whose producer has an `-Id` parameter; verified in the catalog).
* Credentials cross the anonymous pipe once per request in a dedicated `secrets` section, are converted to
  `SecureString`/`PSCredential` in the worker immediately, are scrubbed from any outgoing text, and are never logged.
* `-WhatIf` output is captured by the worker's custom `PSHost` and returned as a distinct stream, so the UI can label
  *PowerShell WhatIf* separately from the application's *validated preview*.
* The worker's `PSHost` refuses interactive prompts, so a command that unexpectedly wants input fails fast instead of hanging.
* Telemetry is controlled explicitly: the setup wizard explains MilestonePSTools telemetry and the worker sets
  `VMS_ApplicationInsights__Enabled` for its own process according to the user's choice (default: follow module setting).
