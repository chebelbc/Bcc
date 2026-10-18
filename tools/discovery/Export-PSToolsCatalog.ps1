#requires -Version 5.1
<#
.SYNOPSIS
    Builds a machine-readable catalog of the MilestonePSTools commands installed on this computer.

.DESCRIPTION
    Imports MilestonePSTools into the current Windows PowerShell 5.1 session and inspects every exported command
    using Get-Command, Get-Help and .NET reflection. NO MilestonePSTools command is executed and no connection to
    an XProtect VMS is made: the script only reads command metadata.

    The output uses the same JSON schema as tools/catalog/extract_repo_catalog.py (schemaVersion 1.0), minus the
    classification fields (risk, category, guiSuitability, dependencies). Classification is applied by:
      * the XProtect Admin Assistant when it loads the catalog (CommandClassifier, same rules), or
      * python3 tools/catalog/classify_catalog.py <raw.json> <classified.json> <report.md>

    Side effects you should know about:
      * Importing MilestonePSTools sends an anonymous "module imported" telemetry event to Milestone unless
        telemetry is disabled (see about_Telemetry). Use -DisableTelemetry to disable it for THIS process only
        (sets $env:VMS_ApplicationInsights__Enabled = 'false' before import; nothing is persisted).
      * Nothing is written except the file given in -OutputPath.

.PARAMETER OutputPath
    Path of the JSON file to write.

.PARAMETER ModuleName
    Module to inspect. Defaults to MilestonePSTools.

.PARAMETER RequiredVersion
    Inspect a specific installed version instead of the newest one.

.PARAMETER DisableTelemetry
    Disable MilestonePSTools telemetry for this PowerShell process before importing the module.

.EXAMPLE
    .\Export-PSToolsCatalog.ps1 -OutputPath .\catalog.runtime.json -DisableTelemetry
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $OutputPath,

    [string] $ModuleName = 'MilestonePSTools',

    [version] $RequiredVersion,

    [switch] $DisableTelemetry
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

function Get-PrivateField {
    param($Object, [string] $Name)
    try {
        $f = $Object.GetType().GetField($Name, [Reflection.BindingFlags]'NonPublic, Instance')
        if ($null -ne $f) { return $f.GetValue($Object) }
    } catch { }
    return $null
}

function Get-RequirementInfo {
    param([object[]] $Attributes)
    $req = [ordered]@{
        vmsConnection      = $null
        minVmsVersion      = $null
        vmsFeatures        = @()
        elevation          = $false
        interactiveSession = $false
        windowsUser        = $false
        documented         = @()
    }
    foreach ($a in $Attributes) {
        $typeName = $a.GetType().Name
        switch -Regex ($typeName) {
            '^RequiresVmsConnectionAttribute$' { $req.vmsConnection = [bool]$a.ConnectionRequired }
            '^RequiresVmsVersionAttribute$' {
                try { $req.minVmsVersion = $a.RequiredVersion.MinVersion.ToString(2) } catch { $req.minVmsVersion = "$($a.RequiredVersion)" }
            }
            '^RequiresVmsFeatureAttribute$' { $req.vmsFeatures += [string]$a.Feature }
            '^RequiresElevationAttribute$' { $req.elevation = $true }
            '^RequiresInteractiveSessionAttribute$' { $req.interactiveSession = $true }
            '^RequiresVmsWindowsUserAttribute$' { $req.windowsUser = $true }
        }
        if ($typeName -like 'Requires*' -and $a.PSObject.Properties['Description']) {
            $req.documented += [string]$a.Description
        }
    }
    $req.vmsFeatures = @($req.vmsFeatures | Sort-Object -Unique)
    $req
}

function Get-CommandAttributes {
    param([System.Management.Automation.CommandInfo] $Command)
    if ($Command -is [System.Management.Automation.CmdletInfo]) {
        return @($Command.ImplementingType.GetCustomAttributes($true))
    }
    if ($Command -is [System.Management.Automation.FunctionInfo]) {
        return @($Command.ScriptBlock.Attributes)
    }
    return @()
}

function Get-ShortTypeName {
    param([type] $Type)
    if ($null -eq $Type) { return 'Object' }
    if ($Type.IsArray) { return (Get-ShortTypeName $Type.GetElementType()) + '[]' }
    if ($Type.IsGenericType -and $Type.GetGenericTypeDefinition() -eq [Nullable`1]) {
        return Get-ShortTypeName $Type.GetGenericArguments()[0]
    }
    return $Type.Name
}

function Get-ParameterInfo {
    param(
        [System.Management.Automation.CommandInfo] $Command,
        $Help
    )
    $common = [System.Management.Automation.Cmdlet]::CommonParameters + [System.Management.Automation.Cmdlet]::OptionalCommonParameters
    $result = New-Object System.Collections.Generic.List[object]
    foreach ($p in $Command.Parameters.Values) {
        if ($common -contains $p.Name) { continue }
        $elementType = if ($p.ParameterType.IsArray) { $p.ParameterType.GetElementType() } else { $p.ParameterType }
        $accepted = @()
        $validators = @()
        $mipItemType = $null
        $validItemTypes = @()
        $minVmsVersion = $null
        $vmsFeature = $null
        foreach ($a in $p.Attributes) {
            $n = $a.GetType().Name
            switch ($n) {
                'ValidateSetAttribute' { $accepted = @($a.ValidValues) }
                'MipItemTransformationAttribute' {
                    $t = Get-PrivateField $a '_type'
                    if ($t) { $mipItemType = $t.Name }
                }
                'ValidateVmsItemTypeAttribute' {
                    $d = Get-PrivateField $a '_itemTypes'
                    if ($d) { $validItemTypes = @($d.Keys) }
                }
                'ValidateVmsVersionAttribute' {
                    try { $minVmsVersion = $a.RequiredVersion.MinVersion.ToString(2) } catch { }
                }
                'ValidateVmsFeatureAttribute' { $vmsFeature = [string]$a.Feature }
                default {
                    if ($n -match '^Validate(Range|Pattern|NotNullOrEmpty|NotNull|Script|Count|Length)Attribute$') {
                        $validators += ($n -replace 'Attribute$')
                    }
                }
            }
        }
        if (-not $accepted -and $elementType.IsEnum) { $accepted = [enum]::GetNames($elementType) }

        $description = ''
        $default = $null
        $wildcard = $false
        if ($Help -and $Help.PSObject.Properties['parameters'] -and $Help.parameters -and $Help.parameters.PSObject.Properties['parameter']) {
            $hp = @($Help.parameters.parameter) | Where-Object { $_.name -eq $p.Name } | Select-Object -First 1
            if ($hp) {
                if ($hp.PSObject.Properties['description'] -and $hp.description) { $description = (@($hp.description) | ForEach-Object { $_.Text }) -join "`n" }
                if ($hp.PSObject.Properties['defaultValue'] -and $hp.defaultValue -and $hp.defaultValue -ne 'None') { $default = [string]$hp.defaultValue }
                if ($hp.PSObject.Properties['globbing']) { $wildcard = "$($hp.globbing)" -eq 'true' }
            }
        }

        $sets = foreach ($kv in $p.ParameterSets.GetEnumerator()) {
            [ordered]@{
                set       = $kv.Key
                mandatory = [bool]$kv.Value.IsMandatory
                position  = if ($kv.Value.Position -ge 0) { $kv.Value.Position } else { $null }
            }
        }
        $info = [ordered]@{
            name                            = $p.Name
            type                            = Get-ShortTypeName $p.ParameterType
            typeFullName                    = $p.ParameterType.FullName
            description                     = $description
            aliases                         = @($p.Aliases)
            acceptedValues                  = @($accepted)
            defaultValue                    = $default
            acceptsWildcards                = $wildcard
            sets                            = @($sets)
            valueFromPipeline               = [bool](@($p.ParameterSets.Values | Where-Object ValueFromPipeline).Count)
            valueFromPipelineByPropertyName = [bool](@($p.ParameterSets.Values | Where-Object ValueFromPipelineByPropertyName).Count)
            mandatoryInAnySet               = [bool](@($p.ParameterSets.Values | Where-Object IsMandatory).Count)
        }
        if ($mipItemType) { $info.mipItemType = $mipItemType }
        if ($validItemTypes) { $info.validItemTypes = $validItemTypes }
        if ($minVmsVersion) { $info.minVmsVersion = $minVmsVersion }
        if ($vmsFeature) { $info.vmsFeature = $vmsFeature }
        if ($validators) { $info.validators = @($validators | Sort-Object -Unique) }
        $result.Add($info)
    }
    , $result.ToArray()
}

# ---------------------------------------------------------------- environment
$ndpRelease = $null
try { $ndpRelease = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -Name Release).Release } catch { }
$isAdmin = $false
try {
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
} catch { }

if ($DisableTelemetry) { $env:VMS_ApplicationInsights__Enabled = 'false' }

$importArgs = @{ Name = $ModuleName; PassThru = $true; ErrorAction = 'Stop' }
if ($RequiredVersion) { $importArgs.RequiredVersion = $RequiredVersion }
$module = Import-Module @importArgs | Select-Object -First 1

$deprecations = @{}
$deprecationsPath = Join-Path $module.ModuleBase 'deprecations.psd1'
if (Test-Path $deprecationsPath) { $deprecations = Import-PowerShellDataFile -Path $deprecationsPath }

$aliasesByCommand = @{}
Get-Command -Module $ModuleName -CommandType Alias | ForEach-Object {
    $target = $_.ResolvedCommandName
    if (-not $target) { return }
    if (-not $aliasesByCommand.ContainsKey($target)) { $aliasesByCommand[$target] = New-Object System.Collections.Generic.List[string] }
    $aliasesByCommand[$target].Add($_.Name)
}

# ---------------------------------------------------------------- commands
$commands = New-Object System.Collections.Generic.List[object]
$all = @(Get-Command -Module $ModuleName -CommandType Cmdlet, Function | Sort-Object Name)
$i = 0
foreach ($cmd in $all) {
    $i++
    Write-Progress -Activity 'Reading command metadata' -Status $cmd.Name -PercentComplete (100 * $i / $all.Count)
    $help = $null
    try { $help = Get-Help -Name $cmd.Name -Full -ErrorAction Stop } catch { }
    $attrs = Get-CommandAttributes $cmd

    $supportsShouldProcess = $false
    $confirmImpact = $null
    foreach ($a in $attrs) {
        if ($a -is [System.Management.Automation.CmdletAttribute] -or $a -is [System.Management.Automation.CmdletBindingAttribute]) {
            $supportsShouldProcess = [bool]$a.SupportsShouldProcess
            if ($supportsShouldProcess) { $confirmImpact = [string]$a.ConfirmImpact }
        }
    }

    $synopsis = ''
    $description = ''
    $helpUri = $cmd.HelpUri
    $examples = @()
    $documented = $false
    if ($help) {
        $synopsis = ([string]$help.Synopsis).Trim()
        # Get-Help returns the bare syntax as the synopsis when no help content exists.
        $documented = $synopsis -and -not $synopsis.StartsWith($cmd.Name + ' ') -and $synopsis -ne $cmd.Name
        if ($help.PSObject.Properties['description'] -and $help.description) {
            $description = (@($help.description) | ForEach-Object { $_.Text }) -join "`n"
        }
        if ($help.PSObject.Properties['examples'] -and $help.examples -and $help.examples.PSObject.Properties['example']) {
            $examples = foreach ($ex in @($help.examples.example)) {
                [ordered]@{
                    title   = ([string]$ex.title).Trim('-', ' ')
                    code    = [string]$ex.code
                    remarks = (@($ex.remarks) | ForEach-Object { $_.Text }) -join "`n"
                }
            }
        }
    }

    $parameters = Get-ParameterInfo -Command $cmd -Help $help
    $sets = foreach ($ps in $cmd.ParameterSets) {
        [ordered]@{
            name       = $ps.Name
            isDefault  = [bool]$ps.IsDefault
            syntax     = "$($cmd.Name) $($ps.ToString())"
            parameters = @(foreach ($pp in $ps.Parameters) {
                    if (([System.Management.Automation.Cmdlet]::CommonParameters + [System.Management.Automation.Cmdlet]::OptionalCommonParameters) -contains $pp.Name) { continue }
                    [ordered]@{ name = $pp.Name; mandatory = [bool]$pp.IsMandatory; position = if ($pp.Position -ge 0) { $pp.Position } else { $null } }
                })
        }
    }

    $commands.Add([ordered]@{
            name                  = $cmd.Name
            verb                  = $cmd.Verb
            noun                  = $cmd.Noun
            commandType           = [string]$cmd.CommandType
            implementation        = if ($cmd.CommandType -eq 'Cmdlet') { 'C#' } else { 'PowerShell' }
            sourceFile            = $null
            helpUri               = $helpUri
            synopsis              = $synopsis
            description           = $description
            documented            = [bool]$documented
            aliases               = @(if ($aliasesByCommand.ContainsKey($cmd.Name)) { $aliasesByCommand[$cmd.Name] | Sort-Object })
            deprecated            = $deprecations.ContainsKey($cmd.Name)
            replacedBy            = if ($deprecations.ContainsKey($cmd.Name)) { $deprecations[$cmd.Name] } else { $null }
            internalHelper        = -not $documented
            supportsShouldProcess = $supportsShouldProcess
            confirmImpact         = $confirmImpact
            requirements          = Get-RequirementInfo $attrs
            parameterSets         = @($sets)
            parameters            = $parameters
            inputs                = @()
            outputTypes           = @($cmd.OutputType | ForEach-Object { $_.Name })
            examples              = @($examples)
        })
}
Write-Progress -Activity 'Reading command metadata' -Completed

# If help content is broadly unavailable (help files missing/blocked), "undocumented" says nothing about
# whether a command is an internal helper, so do not flag anything in that case.
$discrepancies = New-Object System.Collections.Generic.List[object]
$documentedCount = @($commands | Where-Object { $_.documented }).Count
if ($commands.Count -gt 0 -and $documentedCount / $commands.Count -lt 0.5) {
    foreach ($c in $commands) { $c.internalHelper = $false }
    $discrepancies.Add([ordered]@{ command = '*'; issue = "Help content was found for only $documentedCount of $($commands.Count) commands; internal-helper detection disabled." })
} else {
    foreach ($c in $commands) {
        if ($c.internalHelper) { $discrepancies.Add([ordered]@{ command = $c.name; issue = 'Exported but has no help content (possible internal helper).' }) }
    }
}

$catalog = [ordered]@{
    schemaVersion  = '1.0'
    source         = [ordered]@{
        kind       = 'runtime'
        modulePath = $module.ModuleBase
        note       = 'Generated by Export-PSToolsCatalog.ps1 from the installed module. No MilestonePSTools command was executed.'
    }
    moduleName     = $module.Name
    moduleVersion  = $module.Version.ToString()
    generatedAtUtc = [datetime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
    environment    = [ordered]@{
        osVersion             = [Environment]::OSVersion.VersionString
        is64BitProcess        = [Environment]::Is64BitProcess
        psVersion             = $PSVersionTable.PSVersion.ToString()
        psEdition             = [string]$PSVersionTable.PSEdition
        clrVersion            = [string]$PSVersionTable.CLRVersion
        netFrameworkRelease   = $ndpRelease
        languageMode          = [string]$ExecutionContext.SessionState.LanguageMode
        executionPolicy       = [string](Get-ExecutionPolicy)
        isElevated            = $isAdmin
        telemetryDisabledHere = [bool]$DisableTelemetry
    }
    deprecations   = $deprecations
    discrepancies  = $discrepancies.ToArray()
    commands       = $commands.ToArray()
}

$json = $catalog | ConvertTo-Json -Depth 12
$fullPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
[IO.File]::WriteAllText($fullPath, $json, (New-Object Text.UTF8Encoding($false)))
Write-Host "Catalog of $($commands.Count) commands from $($module.Name) $($module.Version) written to $fullPath"
