# Development

## Prerequisites

| To... | You need |
|---|---|
| Build and run the application | Windows x64, .NET 8 SDK (or Visual Studio 2022 with the *.NET desktop development* workload), .NET Framework 4.8 runtime, Windows PowerShell 5.1, and MilestonePSTools installed for real use |
| Run the tests and tooling on any OS | .NET 8 SDK, Python 3.8+, optionally `pwsh` |

The repository contains no third-party runtime dependencies; NuGet is used only for build- and test-time packages
(see THIRD-PARTY-NOTICES.md).

## Repository layout

```
catalog/                     command catalog, classification rules, GUI support map (data, versioned)
docs/                        investigation, generated command inventory, ADRs
src/
  MilestoneAdmin.Protocol/   worker protocol + JSON (shared)
  MilestoneAdmin.Worker.Engine/  PowerShell hosting engine (net48 + net8.0 for tests)
  MilestoneAdmin.Worker/     worker executable
  MilestoneAdmin.Domain/     catalog, classification, capabilities, change plans, CSV, scripts
  MilestoneAdmin.PSTools/    MilestonePSTools command builders, DTO mapping, worker client
  MilestoneAdmin.Application/  workflows and services
  MilestoneAdmin.Infrastructure/  stores, Credential Manager, audit log
  MilestoneAdmin.UI/         WPF application
tests/
  MilestoneAdmin.Tests/      xUnit tests
  MockModules/MilestonePSTools/  test double of the module
tools/
  catalog/                   static extractor, classifier, report/doc generators (Python)
  discovery/                 runtime discovery (PowerShell)
  verify/                    XAML lint + UI compile check for non-Windows machines
```

## Build and run (Windows)

```powershell
dotnet build XProtectAdminAssistant.sln -c Release
.\src\MilestoneAdmin.UI\bin\Release\net48\XProtectAdminAssistant.exe
```

The UI project builds the net48 x64 worker and copies it to `worker\` next to `XProtectAdminAssistant.exe`. The fallback
catalog is copied to `catalog\`. The whole `bin\Release\net48` folder is the application; the installer below packages
it. Sign `XProtectAdminAssistant.exe` and `worker\MilestoneAdmin.Worker.exe` with your organization's code signing
certificate before distribution (they are not signed by CI). Do not add `System.Management.Automation.dll` to the output: the worker must
use the installed Windows PowerShell, and CI checks this.

### Installer

`installer/XProtectAdminAssistant.iss` is an [Inno Setup](https://jrsoftware.org/isinfo.php) 6 script. CI builds it
on Windows after the solution build:

```powershell
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" /DAppVersion=0.1.0 /DSourceDir="$PWD\src\MilestoneAdmin.UI\bin\Release\net48" installer\XProtectAdminAssistant.iss
```

The installer and a portable zip are written to `artifacts\`. The version comes from `Directory.Build.props`.

## Build and test (Linux/macOS)

The WPF project cannot be built outside Windows. Everything else can:

```bash
dotnet test tests/MilestoneAdmin.Tests                           # builds engine, worker (net8.0), libraries
python3 -m unittest discover -s tools/catalog -p "test_*.py"
python3 tools/verify/xaml_lint.py .                              # XAML: XML, resources, handlers, binding paths
dotnet build tools/verify/UiCompileCheck/UiCompileCheck.csproj   # UI C# against WPF reference assemblies
```

`UiCompileCheck` compiles every C# file of the UI project against the .NET Framework 4.8 WPF reference assemblies.
`tools/verify/generate_xaml_stubs.py` generates stand-ins for the members the XAML compiler would create
(`InitializeComponent`, `x:Name` fields). It is a verification aid only. The real build on Windows uses the real
XAML compiler.

## Updating the command catalog

When a new MilestonePSTools version is released:

```bash
git clone https://github.com/milestonesys/MilestonePSTools ../MilestonePSTools
python3 tools/catalog/extract_repo_catalog.py ../MilestonePSTools catalog/milestonepstools-catalog.json docs/COMMAND_INVENTORY.md
python3 tools/catalog/generate_docs.py
dotnet test tests/MilestoneAdmin.Tests
```

Then, on Windows with the new version installed, run `tools/discovery/Export-PSToolsCatalog.ps1` and compare (see
TESTING.md). Update `EnvironmentService.ValidatedModuleVersion` only after the manual checks pass.

Classification lives in `catalog/classification-rules.json`. Change risk levels, categories, sensitive commands or
object types there, never in code. Python (`tools/catalog/classification.py`) and C# (`CommandClassifier`) both read
it, and `ClassifierParityTests` keeps them identical. `CatalogDriftTests` checks that the worker's `CommandPolicy` lists
still match it.

## Adding a quick action

When a guided change is just one documented command with a few inputs, add it to
`src/MilestoneAdmin.Application/Actions/QuickActionCatalog.cs` instead of writing a workflow. Each entry gives:
* the command, plus the parameter set if needed;
* the parameter that receives the selected objects;
* the inputs to show (`QuickActionField`, optionally prefilled from a property of the selected object);
* fixed values, warnings taken from the command documentation, and strong confirmation for destructive actions.

The inventory page whose tab or object type matches `ObjectType` shows the action automatically. Then:
1. Run the tests. `CatalogDriftTests` fail if the command, a parameter or a fixed value does not exist in MilestonePSTools.
2. Add the command to `catalog/gui-support.json` and run `tools/catalog/generate_docs.py`.
3. Add an end-to-end test to `tests/MilestoneAdmin.Tests/EndToEnd/FeatureEndToEndTests.cs` (mock commands use real names only).

## Adding a guided feature

1. **Verify the commands.** Find them in `catalog/milestonepstools-catalog.json` and read their documentation and
   source in the MilestonePSTools repository. Do not guess parameters.
2. **Build the invocation** in `src/MilestoneAdmin.PSTools/Commands/PsCommands.cs` with typed `ParameterValue`s, and
   add a sample call to `PsCommands.Samples()`. The drift test then verifies every command and parameter name.
3. **Expose it** through `VmsGateway` (a `Plan...` method returning a `VmsOperation`). Set `AllowStateChange` and the
   risk level, and mark secrets with `AddSecret`.
4. **Declare a capability** in `CapabilityCatalog` listing the required commands, so the UI can disable the feature
   with a reason when they are missing or the XProtect version is too old.
5. **Write the workflow** in `MilestoneAdmin.Application/Workflows`. Build a `ChangePlan` (diff, warnings, script
   preview, `WhatIfAvailable` from the catalog's `SupportsShouldProcess`, strong confirmation for destructive or bulk
   work). Apply it through `BulkOperationRunner` and `OperationContext`, so results, audit and error translation come for free.
6. **Add the view model and view** in the UI. Use `ReviewViewModel` for review → WhatIf → apply.
7. **Update the mock module** if the end-to-end tests need the command. Use real names only.
8. **List the command** in `catalog/gui-support.json`, run `tools/catalog/generate_docs.py`, and add tests.

## Conventions

* C# 9 (`LangVersion` in `Directory.Build.props`), nullable reference types off, no implicit usings.
* Libraries target netstandard2.0. The UI and worker target net48. Engine and worker also target net8.0 for tests.
* Hand-written MVVM (`ObservableObject`, `RelayCommand`, `AsyncCommand`); no UI framework packages.
* Never call `AddScript`. Never build command text from user input. Never parse formatted PowerShell output.
* Never write a secret to a string that can reach a log, file, script, audit record or exception message.
