# Security

XProtect Admin Assistant can change the configuration of a video management system. Wrong or unauthorized changes
can stop recording, delete recordings or open access to video. This document describes the protections the
application implements, and their limits.

Report vulnerabilities privately to the repository owner (GitHub security advisories for this repository). Do not open
public issues for them.

## Threat model in brief

| Asset | Threats considered |
|---|---|
| XProtect credentials, hardware passwords, tokens | Disclosure through files, logs, generated scripts, screenshots of the UI, crash output or telemetry |
| XProtect configuration and recordings | Accidental destructive changes; injection of unintended commands through UI inputs or imported files |
| The workstation | Unwanted elevation; silent changes to PowerShell configuration |
| The audit trail | Silent editing or deletion of records |

Out of scope: a compromised workstation or Windows account. Anyone who can run code as the user can also use that
user's XProtect access and stored credentials. The application relies on XProtect's own role-based permissions for
authorization.

## Credentials and secrets

* **No plain-text passwords on disk.** Settings, connection profiles, jobs, the audit log, the diagnostics log and the
  catalog cache contain no secrets by design. Connection profiles store the server address, authentication type and
  user name only.
* **Saved passwords** go to **Windows Credential Manager** as generic credentials. They are protected by DPAPI for the
  current Windows user, persisted on this computer only (`CRED_PERSIST_LOCAL_MACHINE`, so they do not roam), and can
  be viewed and removed in *Control Panel → Credential Manager*. Saving a password is optional. **Current Windows user**
  (integrated authentication) is the preferred mode and needs no stored password.
* **In memory**, password boxes hand `SecureString` copies to view models. They are never bound as `string` properties.
* **To the worker**, secrets travel in a dedicated `secrets` section of the request, over the worker's private
  stdin pipe. The worker converts them to `SecureString`/`PSCredential` immediately. Requests are never logged.
  Diagnostics log only operation titles and sanitized worker messages. `InvokeRequest.ToRedactedJson` exists for any
  future diagnostic that needs a request body.
* **Scrubbing.** Every string the worker sends back (errors, warnings, verbose/information messages, results) has the
  request's secret values replaced with `********`, in case a command or exception echoes them.
* **Generated PowerShell** (Expert Mode) never contains secrets. Passwords become
  `Read-Host -AsSecureString` and credentials become `Get-Credential` prompts.
* **Secret-returning commands** (`Get-VmsHardwarePassword`, `Get-VmsToken`, `Get-VmsConnectionString`, and
  `Get-VmsCameraReport -IncludePlainTextPasswords`) are classified *Sensitive*. The worker refuses to return their
  output unless the request explicitly allows sensitive output. In the GUI, revealing a hardware password requires
  Expert Mode and a typed confirmation, and is written to the audit log without the password.
* **CSV files with passwords** (the `Import-VmsHardware` format allows a `Password` column) trigger a warning. Values
  are masked in the preview and held as `SecureString`. `Export-VmsHardware` writes plain-text passwords
  (see docs/INVESTIGATION.md), so it is classified Sensitive and not offered as a guided export.
* **Telemetry.** The application sends no telemetry of its own. MilestonePSTools has its own telemetry. By default
  the application sets `VMS_ApplicationInsights__Enabled=false` for its worker process. The user can choose to follow
  the module's own setting instead. The application never changes the module-wide setting (`Set-VmsModuleConfig`).

## PowerShell injection protection

* The GUI never produces PowerShell **text** for execution. Requests contain command names and **typed** parameter
  values (literal, switch, list, table, secret, credential, object handle, object reference).
* The worker builds pipelines exclusively with `PowerShell.AddCommand` / `AddParameter`. **`AddScript` is not used
  anywhere.**
* Command names must belong to the allowlist of commands exported by the loaded MilestonePSTools module, and are
  invoked module-qualified. Names containing script characters are rejected before lookup.
* `ScriptBlock`-typed parameters are refused, so no code can be smuggled in as an argument. Interactive parameters
  (`-ShowDialog`) and engine-controlled common parameters (`-WhatIf`, `-Confirm`, `-ErrorAction`, ...) cannot be set
  from the GUI.
* Mutating commands (any verb outside the read-only/session set) run only when the request says `allowStateChange`,
  which the GUI sets only for reviewed and confirmed operations.
* Objects are referenced by worker handle or by type + Id, never by name, because MilestonePSTools resolves strings
  by name and fails on duplicates.
* The values shown in generated scripts and in the audit log are quoted by `PowerShellLiteral.Quote`, which handles
  every single-quote character PowerShell recognizes (including typographic quotes). Tests parse the output with the
  PowerShell parser to prove that a value stays one string literal.
* CSV exports escape values that a spreadsheet would treat as formulas (`=`, `+`, `-`, `@`, tab, CR).

## Preventing accidental damage

* **Review before apply** for every change. The review shows a per-object diff (current → new), warnings, and either
  the application's *validated preview* or, where the command supports ShouldProcess, MilestonePSTools'
  *PowerShell -WhatIf* output. The two are always labelled differently. A dry run is never implied when the command
  does not support one.
* **Strong confirmation** (typing the object name or a count) for deletes and other high-impact or large operations.
* **Connection guard.** MilestonePSTools commands try to connect automatically when no session exists, using a saved
  default profile or a login dialog. The worker blocks this and requires an explicit, user-initiated connection.
* **Non-interactive host.** Any prompt from a command fails immediately instead of waiting for input that cannot be
  given.
* **Bulk operations** report per-object results and never hide partial failures. Failed items can be retried or
  exported.

## Least privilege and system changes

* The application manifest requests `asInvoker`. The application **never elevates itself**. Commands that require
  elevation are flagged in pre-checks and Command Explorer.
* The application **never silently** changes the execution policy, trusts the PowerShell Gallery, installs package
  providers, installs modules or elevates. The setup wizard detects these conditions, explains them and shows the
  relevant command. For a missing module, it can open a **visible** Windows PowerShell window that runs
  `Install-Module MilestonePSTools -Scope CurrentUser`, but only after a confirmation dialog that shows the exact
  command. PowerShell's own prompts (NuGet provider, untrusted repository) are answered by the user in that window. The MilestonePSTools README mentions the `RemoteSigned` policy and
  `Install-Module MilestonePSTools [-Scope CurrentUser]`.
* The MIP SDK EULA is accepted (`Connect-Vms -AcceptEula`) only after the user explicitly accepts it in the
  application.

## Audit log

* Every state-changing or sensitive operation is recorded with: timestamp, Windows user, workstation, XProtect system
  and user, operation, target, sanitized command, duration, result, error, correlation id and module version.
* Records are append-only JSON Lines in `%LOCALAPPDATA%\XProtectAdminAssistant\audit\audit.jsonl`. Each record holds
  the SHA-256 hash of the previous one. **Verify** in the Audit page reports the first record that was edited,
  removed or is unreadable.
* The chain is **tamper-evident, not tamper-proof**: someone with write access to the file could rewrite the whole
  chain. For stronger guarantees, export the log regularly to a write-once location. The XProtect server-side audit
  log (if enabled) remains the authoritative record of configuration changes.

## Testing policy

Automated tests never connect to a real XProtect system. They use a mock module whose command and parameter names
are verified against the real catalog. **Do not run destructive operations against an XProtect environment you do
not own or that is not a dedicated test system.** See TESTING.md.
