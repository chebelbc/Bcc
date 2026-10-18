# TEST DOUBLE - NOT MILESTONE SOFTWARE.
# A tiny in-memory imitation of a subset of MilestonePSTools used by the automated tests of the
# XProtect Admin Assistant worker engine. Command and parameter names mirror the real module
# (verified against catalog/milestonepstools-catalog.json by CatalogDriftTests); behaviour is simulated.
@{
    RootModule        = 'MilestonePSTools.psm1'
    ModuleVersion     = '25.2.61'
    GUID              = 'b3a4d5c1-0000-4000-8000-000000000001'
    Author            = 'XProtect Admin Assistant tests'
    Description       = 'Test double of MilestonePSTools for automated tests. Not Milestone software.'
    PowerShellVersion = '5.1'
    ScriptsToProcess  = @('MockTypes.ps1')
    FunctionsToExport = @(
        'Connect-Vms', 'Disconnect-Vms', 'Test-VmsConnection', 'Get-VmsSite', 'Get-VmsManagementServer',
        'Get-VmsRecordingServer', 'Get-VmsHardware', 'Get-VmsCamera', 'Set-VmsCamera', 'Set-VmsHardware',
        'Remove-VmsHardware', 'Get-VmsHardwareDriver', 'Start-VmsHardwareScan', 'Add-VmsHardware', 'Get-VmsStorage',
        'Get-VmsDeviceStatus', 'Get-VmsRole', 'Get-VmsHardwarePassword', 'Get-VmsModuleConfig', 'Wait-VmsTask',
        'Select-Camera', 'Set-VmsDeviceStorage', 'Invoke-MockScriptBlockProbe',
        'Select-VmsSite', 'Get-VmsBasicUser', 'New-VmsBasicUser', 'Set-VmsBasicUser', 'Remove-VmsBasicUser', 'New-VmsRole',
        'Get-VmsRoleMember', 'Add-VmsRoleMember', 'Remove-VmsRoleMember', 'Get-VmsRule', 'Set-VmsRule', 'Get-VmsViewGroup',
        'New-VmsViewGroup', 'Set-VmsViewGroup', 'Remove-VmsViewGroup', 'Add-VmsStorage', 'Remove-VmsStorage', 'New-VmsDeviceGroup',
        'Add-VmsDeviceGroupMember', 'Import-VmsHardware'
    )
    CmdletsToExport   = @()
    AliasesToExport   = @('Get-Camera')
}
