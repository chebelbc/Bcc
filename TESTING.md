# Testing

## Principles

* **No test ever connects to a real XProtect system.** Automated tests use an in-memory test double of
  MilestonePSTools. Destructive behaviour is only ever exercised against that double.
* **The double cannot drift into fiction.** `CatalogDriftTests` fail if the mock module defines a command or parameter
  that does not exist in the catalog extracted from the official MilestonePSTools repository. The same holds for
  anything the application builds.
* **The real engine is under test.** The worker engine multi-targets net8.0. Tests run the production `WorkerEngine`
  in a real PowerShell runspace (PowerShell SDK 7.4), and end-to-end tests start the real worker **process** and talk
  to it over the JSON-lines protocol.

## Running

```bash
# .NET tests (any OS with the .NET 8 SDK; no Windows or MilestonePSTools needed)
dotnet test tests/MilestoneAdmin.Tests

# Catalog tooling (Python 3.8+)
python3 -m unittest discover -s tools/catalog -p "test_*.py"

# UI verification on non-Windows (see DEVELOPMENT.md)
python3 tools/verify/xaml_lint.py .
dotnet build tools/verify/UiCompileCheck/UiCompileCheck.csproj
```

CI (`.github/workflows/ci.yml`) runs all of the above on Linux, and builds the real WPF application and the net48
worker on Windows.

## Test suites

| Suite | File(s) | What it proves |
|---|---|---|
| Worker engine | `Engine/WorkerEngineTests.cs` | Structured output (never console text); allowlist and command-name validation; injection strings bound as literals; ScriptBlock, unknown or abbreviated parameters refused; state changes need approval; ConfirmImpact=High runs without prompting after approval; WhatIf captured as its own stream and changes nothing; WhatIf refused without ShouldProcess; no auto-connect; interactive commands refused; secret scrubbing and redaction; sensitive-output gate; handles and Id re-resolution; per-object non-terminating errors; streaming; cancellation; timeouts; runtime discovery; environment probe |
| End to end | `EndToEnd/EndToEndTests.cs` | Real worker process plus application services: connect and catalog discovery, error translation (wrong password, unreachable server), bulk camera change with review/preview/apply/audit, partial failures with retry, the Add Hardware workflow, hardware removal with strong confirmation and WhatIf, CSV onboarding, reports, password reveal audited without the value, worker crash handling |
| Catalog drift | `Domain/CatalogDriftTests.cs` | Every command and parameter used by `PsCommands` and by every quick action (including fixed values against accepted values), every object producer, and every mock function and parameter exists in the catalog; worker policy lists equal the rules file; `gui-support.json` covers every command the app builds |
| Classifier parity | `Domain/ClassifierParityTests.cs` | The C# classifier gives the same risk, category, suitability, controls, secret flags and dependencies as the Python classifier for all 292 commands |
| Domain units | `Domain/DomainUnitTests.cs` | JSON parser and writer (escapes, depth limit, malformed input); CSV (quoting, BOM, line numbers, formula-injection escaping); hardware CSV validation ("N rows detected, X valid, Y invalid", masking, online checks, address rules); PowerShell literal quoting, **parsed back with the PowerShell parser**; script generation without secrets; version rules; diffs |
| Application units | `ApplicationUnitTests.cs` | Error translation by exception type; address ranges and naming patterns; Command Explorer forms produce typed parameters (injection text stays a literal; secrets stay out of pipeline and redacted JSON); blocked suitability levels; audit hash chain detects edits, deletions and corrupt lines |
| Excel | `Domain/XlsxTests.cs` | Reads workbooks produced by openpyxl, XlsxWriter and LibreOffice (fixtures in `tests/Fixtures/xlsx`) into the same table: shared and inline strings, rich text, numbers, booleans, blank rows, real row numbers; detects MilestonePSTools export workbooks; rejects DTDs/XXE, non-workbooks, values without a header, duplicate headers; writer output round-trips and stores formulas as text |
| Feature end to end | `EndToEnd/FeatureEndToEndTests.cs` | Excel onboarding with device groups; MilestonePSTools workbook import (and the skip-existing rule); site switching; hardware edit (only changed values sent) and credential update without leaking the password; storage add/remove including the documented refusal for the default storage; role members (partial failure reported, SID-based removal with a reproducible script); basic users; rules (already-disabled rules skipped); view groups (`-Recurse` rule) |
| Discovery script | `Domain/DiscoveryScriptTests.cs` | `tools/discovery/Export-PSToolsCatalog.ps1` runs against the mock module, and its output loads into the application's catalog with correct classification |
| Catalog tooling | `tools/catalog/test_catalog_tools.py` | Extractor on a synthetic repository fixture (platyPS, PowerShell and C# parsing, inheritance, deprecations, discrepancies); classification rules; committed catalog consistency; generated docs up to date |

## The mock module

`tests/MockModules/MilestonePSTools` is labelled **TEST DOUBLE - NOT MILESTONE SOFTWARE**. It simulates a small
site in memory: recording servers, hardware, cameras, storage, roles, drivers and scans. It uses the same command
names, parameter names, requirement attributes (defined via `MockTypes.ps1`) and ShouldProcess behaviour as the
real module. It reproduces behaviour the application depends on, for example:
* `Get-VmsCamera` returns only enabled devices unless `-EnableFilter All` is passed;
* commands carry `[RequiresVmsConnection()]` like their real counterparts, so the worker's connection guard is
  exercised. Where the real module would try to auto-connect, the mock throws instead.

Rules for changing it:

* Add only commands and parameters that exist in the real module. `CatalogDriftTests` enforce this.
* Copy behaviour from the real module's source or documentation, and say where it came from in a comment.
* Requirement attributes must have the real shape. `RequiresVmsVersion` exposes `RequiredVersion` as a
  `VersionInterval`, as in `src/MilestonePSTools/Attributes.cs`; giving the mock the wrong shape once hid a
  discovery-script code path.
* `Invoke-MockScriptBlockProbe` is the one deliberate exception. It exists only to prove that ScriptBlock parameters are refused.

## Manual testing on Windows

The automated tests cannot cover:
* the real WPF rendering and interaction;
* the real MilestonePSTools module;
* the real MIP SDK;
* a real XProtect system.

Before a release:

1. On a Windows workstation with MilestonePSTools installed, run
   `tools/discovery/Export-PSToolsCatalog.ps1 -OutputPath runtime.json -DisableTelemetry`. Classify it with
   `python3 tools/catalog/classify_catalog.py runtime.json runtime-classified.json` and compare it with
   `catalog/milestonepstools-catalog.json`. Investigate every difference.
2. Open a hardware onboarding template, fill it in **with Microsoft Excel**, save it, and import it. The Excel reader is
   tested only with other producers so far.
3. Start the application, run the setup wizard, and connect to a **dedicated test XProtect system**. A Milestone demo
   or lab installation that you own is suitable.
4. Exercise the read-only pages first. Then try each change workflow with **PowerShell -WhatIf** first, then apply it
   on test objects only.
5. Check the audit log (Verify), and confirm that no secrets appear in:
   * `%APPDATA%\XProtectAdminAssistant`;
   * `%LOCALAPPDATA%\XProtectAdminAssistant`;
   * exported scripts and reports.

**Never run destructive workflows against a production system or any system you are not authorized to change.**
