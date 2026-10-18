# Third-Party Notices and Licensing

XProtect Admin Assistant is licensed under the Apache License 2.0 (see [LICENSE](LICENSE)). This file lists the
third-party software it uses and how each piece is used.

## Not redistributed: installed by the user

| Software | License | Notes |
|---|---|---|
| **MilestonePSTools** (Milestone Systems A/S), https://github.com/milestonesys/MilestonePSTools | Apache License 2.0 (repository `LICENSE`, verified at commit `a9701bec`) | Used as the automation engine. **Not bundled.** The user installs it from the PowerShell Gallery. The application loads it, in the worker process, from the user's module path. |
| **Milestone MIP SDK** (`VideoOS.Platform*` assemblies, shipped inside MilestonePSTools) | Milestone MIP SDK end-user license agreement | Accepted by the user through `Connect-Vms -AcceptEula`. The application passes `-AcceptEula` only after the user explicitly accepts the EULA in the setup wizard or Settings. Read the EULA text from Milestone before accepting. |
| Other assemblies shipped inside MilestonePSTools (e.g. Azure.Identity, Microsoft.Extensions.Configuration, CsvHelper) | Their respective licenses, as distributed with MilestonePSTools | Never referenced or redistributed by this application. They are loaded only into the worker process, by the module. |
| Windows PowerShell 5.1, .NET Framework 4.8 | Part of Windows / Microsoft license terms | System components. |

## Build-time only: not part of the shipped application

| Package | Use | License |
|---|---|---|
| Microsoft.NETFramework.ReferenceAssemblies.net48 | Compile .NET Framework 4.8 targets with the dotnet CLI | MIT (Microsoft) |
| Microsoft.PowerShell.5.ReferenceAssemblies | Compile the worker engine against Windows PowerShell 5.1 APIs. Excluded from the output with `ExcludeAssets="runtime"`; the CI checks that `System.Management.Automation.dll` is not shipped. | Microsoft; see the package's license on nuget.org |

## Installer

The setup program (`XProtectAdminAssistant-Setup-<version>.exe`) is built with **Inno Setup 6** (Jordan Russell and
Martijn Laan, https://jrsoftware.org). It contains Inno Setup's setup engine, distributed under the Inno Setup License.
That license permits free use, including commercially; read it on jrsoftware.org. The portable zip does not contain
Inno Setup.

## Tests only

| Package | License |
|---|---|
| Microsoft.PowerShell.SDK 7.4.6 (and its dependencies) | MIT (PowerShell) |
| xunit 2.9.3, xunit.runner.visualstudio 2.8.2 | Apache License 2.0 |
| Microsoft.NET.Test.Sdk 17.12.0 | MIT |

The Excel test fixtures in `tests/Fixtures/xlsx` were generated once with openpyxl, XlsxWriter and LibreOffice. These
tools are not dependencies of the application or of the tests.

The license information above was taken from each project's published metadata as known at the time of writing.
Check the package pages on nuget.org for the authoritative license of the exact version you use.

## Trademarks

Milestone, XProtect and related product names belong to Milestone Systems A/S. This project is not affiliated with, endorsed
by or supported by Milestone Systems. "MilestonePSTools" refers to the open-source module published by Milestone
Systems.
