# TEST DOUBLE - NOT MILESTONE SOFTWARE.
# Simulates a small XProtect site in memory. Only used by automated tests; never shipped.
# Command/parameter names mirror MilestonePSTools 25.2 (see CatalogDriftTests). Object property names mirror the
# MIP SDK configuration items as used by the real module's own scripts (e.g. Private/ExportHardwareCsv.ps1).

Set-StrictMode -Version 2.0

function New-MockItem([string]$Type, [hashtable]$Properties) {
    $o = [pscustomobject]$Properties
    $o.PSObject.TypeNames.Insert(0, "VideoOS.Platform.ConfigurationItems.$Type")
    $o
}

function Reset-MockSite {
    $script:Connected = $false
    $script:EulaAccepted = $false
    $rs1 = New-MockItem RecordingServer @{
        Name = 'RS01'; HostName = 'rs01.example.local'; WebServerUri = 'http://rs01.example.local:7563/'
        ActiveWebServerUri = 'http://rs01.example.local:7563/'; Id = [guid]'11111111-1111-1111-1111-000000000001'
        Path = 'RecordingServer[11111111-1111-1111-1111-000000000001]'; Enabled = $true; Description = 'Main recorder'
        PublicAccessEnabled = $false; LastModified = [datetime]'2026-01-01T00:00:00Z'
    }
    $rs2 = New-MockItem RecordingServer @{
        Name = 'RS02'; HostName = 'rs02.example.local'; WebServerUri = 'http://rs02.example.local:7563/'
        ActiveWebServerUri = 'http://rs02.example.local:7563/'; Id = [guid]'11111111-1111-1111-1111-000000000002'
        Path = 'RecordingServer[11111111-1111-1111-1111-000000000002]'; Enabled = $true; Description = ''
        PublicAccessEnabled = $false; LastModified = [datetime]'2026-01-01T00:00:00Z'
    }
    $script:RecordingServers = @($rs1, $rs2)
    $script:Hardware = New-Object System.Collections.Generic.List[object]
    $script:Cameras = New-Object System.Collections.Generic.List[object]
    $script:Passwords = @{}
    $script:Storage = @(
        (New-MockItem Storage @{ Name = 'Local default'; Id = [guid]'33333333-0000-0000-0000-000000000001'; Path = 'Storage[33333333-0000-0000-0000-000000000001]'; ParentItemPath = $rs1.Path; DiskPath = 'C:\MediaDatabase'; RetainMinutes = 10080; MaxSize = 512000; IsDefault = $true; Description = '' }),
        (New-MockItem Storage @{ Name = '30 days'; Id = [guid]'33333333-0000-0000-0000-000000000002'; Path = 'Storage[33333333-0000-0000-0000-000000000002]'; ParentItemPath = $rs1.Path; DiskPath = 'D:\Media'; RetainMinutes = 43200; MaxSize = 2048000; IsDefault = $false; Description = '' }),
        (New-MockItem Storage @{ Name = 'Local default'; Id = [guid]'33333333-0000-0000-0000-000000000003'; Path = 'Storage[33333333-0000-0000-0000-000000000003]'; ParentItemPath = $rs2.Path; DiskPath = 'C:\MediaDatabase'; RetainMinutes = 10080; MaxSize = 512000; IsDefault = $true; Description = '' })
    )
    $script:Drivers = @(
        (New-MockItem HardwareDriver @{ Number = 806; Name = 'AXIS P-series'; GroupName = 'AXIS'; DriverType = 'Camera'; Path = 'HardwareDriver[aaaaaaaa-0000-0000-0000-000000000806]'; Id = [guid]'aaaaaaaa-0000-0000-0000-000000000806' }),
        (New-MockItem HardwareDriver @{ Number = 421; Name = 'Universal 16 channel driver'; GroupName = 'Universal'; DriverType = 'Camera'; Path = 'HardwareDriver[aaaaaaaa-0000-0000-0000-000000000421]'; Id = [guid]'aaaaaaaa-0000-0000-0000-000000000421' })
    )
    $script:Roles = @(
        (New-MockItem Role @{ Name = 'Administrators'; Id = [guid]'44444444-0000-0000-0000-000000000001'; Path = 'Role[44444444-0000-0000-0000-000000000001]'; RoleType = 'Adminstrative'; Description = 'Built-in' }),
        (New-MockItem Role @{ Name = 'Operators'; Id = [guid]'44444444-0000-0000-0000-000000000002'; Path = 'Role[44444444-0000-0000-0000-000000000002]'; RoleType = 'UserDefined'; Description = '' })
    )
    $script:Sites = @(
        (New-MockSiteItem 'Mock Site' 'http://mgmt.example.local/'),
        (New-MockSiteItem 'Mock Child Site' 'http://child.example.local/')
    )
    $script:SelectedSite = $script:Sites[0]
    $script:BasicUsers = New-Object System.Collections.Generic.List[object]
    $script:BasicUsers.Add((New-MockItem BasicUser @{ Name = 'operator1'; Id = [guid]'55555555-0000-0000-0000-000000000001'; Path = 'BasicUser[55555555-0000-0000-0000-000000000001]'; Description = ''; Status = 'Enabled'; CanChangePassword = $true; ForcePasswordChange = $false; IsExternal = $false }))
    $script:BasicUserPasswords = @{}
    $script:Rules = New-Object System.Collections.Generic.List[object]
    foreach ($r in @(@{ n = 'Default Record on Motion Rule'; e = $true }, @{ n = 'Night alarm'; e = $false })) {
        $rid = [guid]::NewGuid()
        $script:Rules.Add((New-MockItem Rule @{ DisplayName = $r.n; Name = $r.n; Enabled = $r.e; Id = $rid; Path = "Rule[$rid]" }))
    }
    $script:ViewGroups = New-Object System.Collections.Generic.List[object]
    $vgid = [guid]::NewGuid()
    $script:ViewGroups.Add((New-MockItem ViewGroup @{ Name = 'Lobby views'; DisplayName = 'Lobby views'; Description = ''; Id = $vgid; Path = "ViewGroup[$vgid]"; ChildCount = 2 }))
    $script:RoleMembers = @{}
    $script:RoleMembers[$script:Roles[1].Path] = New-Object System.Collections.Generic.List[object]
    $script:RoleMembers[$script:Roles[1].Path].Add((New-MockUser 'CONTOSO\jdoe' 'S-1-5-21-1000-1000-1000-1101'))
    $script:RoleMembers[$script:Roles[1].Path].Add((New-MockUser 'CONTOSO\asmith' 'S-1-5-21-1000-1000-1000-1102'))
    $script:DeviceGroups = @{}
    Add-MockHardware -Recorder $rs1 -Name 'Lobby encoder' -Address 'http://10.1.20.10/' -Channels 4 -Enabled $true -Password 'enc0der!'
    Add-MockHardware -Recorder $rs1 -Name 'Parking' -Address 'http://10.1.20.11/' -Channels 1 -Enabled $false -Password 'park1ng!'
    Add-MockHardware -Recorder $rs2 -Name 'Warehouse' -Address 'http://10.1.30.5/' -Channels 1 -Enabled $true -Password 'wareh0use!'
}

function Add-MockHardware($Recorder, [string]$Name, [string]$Address, [int]$Channels, [bool]$Enabled, [string]$Password, [string]$Model = 'AXIS M1065-L Network Camera') {
    $id = [guid]::NewGuid()
    $hw = New-MockItem Hardware @{
        Name = $Name; Address = $Address; Model = $Model; Enabled = $Enabled; UserName = 'root'
        Id = $id; Path = "Hardware[$id]"; ParentItemPath = $Recorder.Path; Description = ''
        HardwareDriverPath = 'HardwareDriver[aaaaaaaa-0000-0000-0000-000000000806]'; LastModified = [datetime]::UtcNow
    }
    $script:Hardware.Add($hw)
    $script:Passwords[$hw.Path] = $Password
    for ($i = 0; $i -lt $Channels; $i++) {
        $cid = [guid]::NewGuid()
        $script:Cameras.Add((New-MockItem Camera @{
                    Name = "$Name - Camera $($i + 1)"; ShortName = ''; Description = ''; Enabled = ($Enabled -and $i -eq 0) -or ($Enabled -and $Channels -gt 1 -and $i -lt 2)
                    Channel = $i; Id = $cid; Path = "Camera[$cid]"; ParentItemPath = $hw.Path
                    RecordingEnabled = $true; RecordingFramerate = 5.0; RecordKeyframesOnly = $false
                    PrebufferEnabled = $true; PrebufferSeconds = 3; RecordingStorage = $script:Storage[0].Path
                    GisPoint = 'POINT EMPTY'; CreatedDate = [datetime]'2026-01-01T00:00:00Z'; LastModified = [datetime]::UtcNow
                }))
    }
    $hw
}

function New-MockSiteItem([string]$Name, [string]$Uri) {
    $site = [pscustomobject]@{ Name = $Name; FQID = "ServerId: $Uri" }
    $site.PSObject.TypeNames.Insert(0, 'VideoOS.Platform.Item')
    $site
}

function New-MockUser([string]$AccountName, [string]$Sid) {
    $uid = [guid]::NewGuid()
    New-MockItem User @{ AccountName = $AccountName.Split('\')[-1]; Domain = $AccountName.Split('\')[0]; DisplayName = $AccountName; Name = $AccountName; Sid = $Sid; IdentityType = 'WindowsUser'; Id = $uid; Path = "User[$Sid]" }
}

function Assert-MockConnected {
    if (-not $script:Connected) { throw 'Mock: not connected. (The real module would try to auto-connect here.)' }
}

function Connect-Vms {
    [CmdletBinding(DefaultParameterSetName = 'ConnectionProfile')]
    [RequiresVmsConnection($false)]
    param(
        [Parameter(ParameterSetName = 'ConnectionProfile')][Parameter(ParameterSetName = 'ShowDialog')][Parameter(ParameterSetName = 'ServerAddress')]
        [string] $Name = 'default',
        [Parameter(ParameterSetName = 'ShowDialog')][switch] $ShowDialog,
        [Parameter(ParameterSetName = 'ServerAddress', Mandatory)][uri] $ServerAddress,
        [Parameter(ParameterSetName = 'ServerAddress')][pscredential] $Credential,
        [Parameter(ParameterSetName = 'ServerAddress')][switch] $BasicUser,
        [Parameter(ParameterSetName = 'ServerAddress')][switch] $SecureOnly,
        [switch] $IncludeChildSites,
        [switch] $AcceptEula,
        [Parameter(ParameterSetName = 'ConnectionProfile')][switch] $NoProfile
    )
    if ($PSCmdlet.ParameterSetName -ne 'ServerAddress') { throw 'Mock: only -ServerAddress is simulated.' }
    if (-not $AcceptEula -and -not $script:EulaAccepted) { throw 'You must accept the Milestone MIP SDK end user license agreement by including -AcceptEula.' }
    if ($AcceptEula) { $script:EulaAccepted = $true }
    if ($ServerAddress.Host -eq 'unreachable.example.local') { throw [System.TimeoutException]::new('The request channel timed out attempting to connect to the management server.') }
    if ($Credential -and $Credential.GetNetworkCredential().Password -eq 'wrong') {
        throw [System.UnauthorizedAccessException]::new("Login failed for user '$($Credential.UserName)'. The password was 'wrong'.")
    }
    if ($SecureOnly -and $ServerAddress.Scheme -ne 'https') { throw 'Mock: SecureOnly requires https.' }
    $script:Connected = $true
    Write-Host "Connected to $ServerAddress (mock)"
    Get-VmsManagementServer
}

function Disconnect-Vms {
    [CmdletBinding()]
    [RequiresVmsConnection($false)]
    param()
    $script:Connected = $false
}

function Test-VmsConnection {
    [CmdletBinding()]
    [RequiresVmsConnection($false)]
    param()
    [bool]$script:Connected
}

function Get-VmsSite {
    [CmdletBinding()]
    [RequiresVmsConnection()]
    param([switch] $ListAvailable, [Parameter(Position = 0)][string] $Name = '*')
    Assert-MockConnected
    if ($ListAvailable) { $script:Sites | Where-Object Name -Like $Name } else { $script:SelectedSite }
}

function Select-VmsSite {
    [CmdletBinding(DefaultParameterSetName = 'ByName')]
    [RequiresVmsConnection()]
    param(
        [Parameter(Mandatory, ValueFromPipeline, ParameterSetName = 'Site')][object] $Site,
        [Parameter(Position = 0, ParameterSetName = 'ByName')][string] $Name,
        [Parameter(ParameterSetName = 'MainSite')][switch] $MainSite
    )
    Assert-MockConnected
    $target = switch ($PSCmdlet.ParameterSetName) {
        'Site' { $script:Sites | Where-Object Name -EQ $Site.Name }
        'ByName' { $script:Sites | Where-Object Name -EQ $Name }
        'MainSite' { $script:Sites[0] }
    }
    if (-not $target) { throw "Site not found." }
    $script:SelectedSite = $target
    $target
}

function Get-VmsManagementServer {
    [CmdletBinding()]
    [RequiresVmsConnection()]
    param()
    Assert-MockConnected
    New-MockItem ManagementServer @{ Name = $script:SelectedSite.Name; ComputerName = $(if ($script:SelectedSite -eq $script:Sites[0]) { 'MGMT01' } else { 'CHILD01' }); Version = '25.2.4815.1'; Id = [guid]'22222222-0000-0000-0000-000000000001'; Description = '' }
}

function Get-VmsRecordingServer {
    [CmdletBinding(DefaultParameterSetName = 'ByName')]
    [RequiresVmsConnection()]
    param(
        [Parameter(ParameterSetName = 'ByName', Position = 0)][string] $Name = '*',
        [Parameter(ParameterSetName = 'ById', Mandatory)][guid] $Id,
        [Parameter(ParameterSetName = 'ByHostname')][string] $HostName = '*'
    )
    Assert-MockConnected
    switch ($PSCmdlet.ParameterSetName) {
        'ById' {
            $r = $script:RecordingServers | Where-Object Id -EQ $Id
            if (-not $r) { throw "RecordingServer with Id $Id not found." }
            $r
        }
        'ByHostname' { $script:RecordingServers | Where-Object HostName -Like $HostName }
        default {
            $r = @($script:RecordingServers | Where-Object Name -Like $Name)
            if ($r.Count -eq 0 -and -not [management.automation.wildcardpattern]::ContainsWildcardCharacters($Name)) {
                Write-Error -Message "No recording server found matching the name '$Name'" -Category ObjectNotFound -TargetObject $Name -ErrorId 'RecordingServerNotFound'
            }
            $r
        }
    }
}

function Get-VmsHardware {
    [CmdletBinding(DefaultParameterSetName = 'All')]
    [RequiresVmsConnection()]
    param(
        [Parameter(ParameterSetName = 'All')][switch] $All,
        [Parameter(ParameterSetName = 'Filtered', ValueFromPipeline)][object] $RecordingServer,
        [Parameter(ParameterSetName = 'Filtered')][guid] $RecorderId,
        [Parameter(ParameterSetName = 'Filtered', Position = 1)][string] $Name = '*',
        [Parameter(ParameterSetName = 'ById')][guid] $Id,
        [Parameter(ParameterSetName = 'All')][Parameter(ParameterSetName = 'Filtered')][ValidateSet('All', 'Enabled', 'Disabled')][string] $EnableFilter = 'Enabled',
        [switch] $CaseSensitive
    )
    process {
        Assert-MockConnected
        if ($PSCmdlet.ParameterSetName -eq 'ById') {
            $h = $script:Hardware | Where-Object Id -EQ $Id
            if (-not $h) { throw [System.Management.Automation.ItemNotFoundException]::new("Hardware with Id $Id not found.") }
            return $h
        }
        $items = $script:Hardware
        if ($RecordingServer) { $items = $items | Where-Object ParentItemPath -EQ $RecordingServer.Path }
        if ($RecorderId) { $items = $items | Where-Object ParentItemPath -EQ "RecordingServer[$RecorderId]" }
        $items = $items | Where-Object Name -Like $Name
        switch ($EnableFilter) {
            'Enabled' { $items | Where-Object Enabled }
            'Disabled' { $items | Where-Object { -not $_.Enabled } }
            default { $items }
        }
    }
}

function Get-VmsCamera {
    [CmdletBinding(DefaultParameterSetName = 'QueryItems')]
    [RequiresVmsConnection()]
    param(
        [Parameter(ParameterSetName = 'QueryItems', Position = 0)][Parameter(ParameterSetName = 'Hardware', Position = 1)][string] $Name = '*',
        [Parameter(ParameterSetName = 'QueryItems', Position = 1)][Parameter(ParameterSetName = 'Hardware', Position = 2)][string] $Description = '*',
        [Parameter(ParameterSetName = 'QueryItems')][Parameter(ParameterSetName = 'Hardware')][int[]] $Channel,
        [Parameter(ParameterSetName = 'QueryItems')][Parameter(ParameterSetName = 'Hardware')][ValidateSet('All', 'Enabled', 'Disabled')][string] $EnableFilter = 'Enabled',
        [Parameter(ParameterSetName = 'QueryItems')][Parameter(ParameterSetName = 'Hardware')][ValidateSet('Equals', 'NotEquals', 'Contains', 'BeginsWith')][string] $Comparison = 'Contains',
        [Parameter(ParameterSetName = 'QueryItems')][int] $MaxResults = [int]::MaxValue,
        [Parameter(ParameterSetName = 'Hardware', Mandatory, ValueFromPipeline, Position = 0)][object[]] $Hardware,
        [Parameter(ParameterSetName = 'Id', Mandatory, Position = 0)][guid[]] $Id,
        [Parameter(ParameterSetName = 'Path', Mandatory, Position = 0)][string[]] $Path
    )
    process {
        Assert-MockConnected
        switch ($PSCmdlet.ParameterSetName) {
            'Id' {
                foreach ($i in $Id) {
                    $c = $script:Cameras | Where-Object Id -EQ $i
                    if (-not $c) { Write-Error "Camera with Id $i not found." -Category ObjectNotFound } else { $c }
                }
                return
            }
            'Path' { return $script:Cameras | Where-Object { $Path -contains $_.Path } }
        }
        $items = $script:Cameras
        if ($Hardware) { $paths = $Hardware | ForEach-Object Path; $items = $items | Where-Object { $paths -contains $_.ParentItemPath } }
        if ($Name -ne '*') { $items = $items | Where-Object { $_.Name -like "*$Name*" } }
        if ($Channel) { $items = $items | Where-Object { $Channel -contains $_.Channel } }
        switch ($EnableFilter) {
            'Enabled' { $items = $items | Where-Object Enabled }
            'Disabled' { $items = $items | Where-Object { -not $_.Enabled } }
        }
        $items | Select-Object -First $MaxResults
    }
}

function Set-VmsCamera {
    [CmdletBinding(SupportsShouldProcess)]
    [RequiresVmsConnection()]
    param(
        [Parameter(Mandatory, ValueFromPipeline, Position = 0)][Alias('Camera')][object[]] $InputObject,
        [string] $Name,
        [string] $ShortName,
        [string] $Description,
        [bool] $Enabled,
        [bool] $RecordingEnabled,
        [double] $RecordingFramerate,
        [bool] $PrebufferEnabled,
        [int] $PrebufferSeconds,
        [switch] $PassThru
    )
    process {
        Assert-MockConnected
        foreach ($cam in $InputObject) {
            if ($PSCmdlet.ShouldProcess($cam.Name, 'Update camera settings')) {
                foreach ($p in 'Name', 'ShortName', 'Description', 'Enabled', 'RecordingEnabled', 'RecordingFramerate', 'PrebufferEnabled', 'PrebufferSeconds') {
                    if ($PSBoundParameters.ContainsKey($p)) {
                        Write-Verbose "Setting $p on $($cam.Name)"
                        $cam.$p = $PSBoundParameters[$p]
                    }
                }
                if ($cam.Name -like '*FAIL*') { Write-Error -Message "Failed to save camera '$($cam.Name)': the configuration API returned an error." -ErrorId 'SaveFailed' -Category WriteError -TargetObject $cam.Name; continue }
                $cam.LastModified = [datetime]::UtcNow
                if ($PassThru) { $cam }
            }
        }
    }
}

function Set-VmsHardware {
    [CmdletBinding(SupportsShouldProcess)]
    [RequiresVmsConnection()]
    param(
        [Parameter(Mandatory, ValueFromPipeline, Position = 0)][object[]] $Hardware,
        [Parameter(Position = 1)][bool] $Enabled,
        [Parameter(Position = 2)][string] $Name,
        [Parameter(Position = 3)][uri] $Address,
        [Parameter(Position = 4)][string] $UserName,
        [Parameter(Position = 5)][securestring] $Password,
        [switch] $UpdateRemoteHardware,
        [Parameter(Position = 6)][string] $Description,
        [switch] $PassThru
    )
    process {
        Assert-MockConnected
        foreach ($hw in $Hardware) {
            if ($PSCmdlet.ShouldProcess($hw.Name, 'Update hardware settings')) {
                foreach ($p in 'Enabled', 'Name', 'UserName', 'Description') { if ($PSBoundParameters.ContainsKey($p)) { $hw.$p = $PSBoundParameters[$p] } }
                if ($PSBoundParameters.ContainsKey('Address')) { $hw.Address = $Address.ToString() }
                if ($PSBoundParameters.ContainsKey('Password')) { $script:Passwords[$hw.Path] = [pscredential]::new('x', $Password).GetNetworkCredential().Password }
                if ($PassThru) { $hw }
            }
        }
    }
}

function Remove-VmsHardware {
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    [RequiresVmsConnection()]
    param([Parameter(Mandatory, ValueFromPipeline, Position = 0)][object[]] $Hardware)
    process {
        Assert-MockConnected
        foreach ($hw in $Hardware) {
            if ($PSCmdlet.ShouldProcess("$($hw.Name) ($($hw.Address))", 'Remove hardware and all child devices')) {
                $script:Hardware.Remove(($script:Hardware | Where-Object Path -EQ $hw.Path)) | Out-Null
                @($script:Cameras | Where-Object ParentItemPath -EQ $hw.Path) | ForEach-Object { $script:Cameras.Remove($_) | Out-Null }
            }
        }
    }
}

function Get-VmsHardwareDriver {
    [CmdletBinding()]
    [RequiresVmsConnection()]
    param(
        [Parameter(Mandatory, ValueFromPipeline, ParameterSetName = 'Hardware')][object[]] $Hardware,
        [Parameter(Mandatory, ValueFromPipeline, ParameterSetName = 'RecordingServer')][object[]] $RecordingServer
    )
    process {
        Assert-MockConnected
        if ($Hardware) { foreach ($h in $Hardware) { $script:Drivers | Where-Object Path -EQ $h.HardwareDriverPath } }
        else { $script:Drivers }
    }
}

function Start-VmsHardwareScan {
    [CmdletBinding()]
    [RequiresVmsConnection()]
    param(
        [Parameter(Mandatory, ValueFromPipeline)][object[]] $RecordingServer,
        [Parameter(ParameterSetName = 'Express')][switch] $Express,
        [Parameter(ParameterSetName = 'Manual')][uri[]] $Address,
        [Parameter(ParameterSetName = 'Manual')][ipaddress] $Start,
        [Parameter(ParameterSetName = 'Manual')][ipaddress] $End,
        [Parameter(ParameterSetName = 'Manual')][string] $Cidr,
        [Parameter(ParameterSetName = 'Manual')][int] $HttpPort = 80,
        [Parameter(ParameterSetName = 'Manual')][int[]] $DriverNumber,
        [Parameter(ParameterSetName = 'Manual')][string[]] $DriverFamily,
        [pscredential[]] $Credential,
        [switch] $UseDefaultCredentials,
        [switch] $UseHttps,
        [switch] $PassThru
    )
    process {
        Assert-MockConnected
        $targets = @()
        if ($Address) { $targets = $Address | ForEach-Object { $_.ToString() } }
        elseif ($Express) { $targets = @('http://10.1.20.55/', 'http://10.1.20.56/') }
        $total = [math]::Max(1, $targets.Count)
        $n = 0
        foreach ($rec in $RecordingServer) {
            foreach ($t in $targets) {
                $n++
                Write-Progress -Activity "Scanning hardware on $($rec.Name)" -Status $t -PercentComplete ([int](100 * $n / $total))
                $user = if ($Credential) { $Credential[0].UserName } else { 'root' }
                $pass = if ($Credential) { $Credential[0].GetNetworkCredential().Password } else { 'pass' }
                $ok = $t -notlike '*.99*'
                if (-not $ok) { Write-Warning "No supported device responded at $t" }
                $r = [pscustomobject]@{
                    HardwareAddress = [uri]$t; UserName = $user; Password = $pass; MacAddress = ('00408C' + ('{0:X6}' -f $n))
                    HardwareDriverPath = if ($ok) { 'HardwareDriver[aaaaaaaa-0000-0000-0000-000000000806]' } else { '' }
                    HardwareScanValidated = $ok; MacAddressExistsLocal = $false; MacAddressExistsGlobal = $false
                    State = if ($ok) { 'Success' } else { 'Error' }; ErrorText = if ($ok) { '' } else { 'No hardware found' }; Progress = 100
                    Path = "Task[$n]"
                }
                $r.PSObject.TypeNames.Insert(0, 'VmsHardwareScanResult')
                Add-Member -InputObject $r -MemberType NoteProperty -Name RecordingServer -Value $rec
                $r
            }
        }
        Write-Progress -Activity 'Scanning hardware' -Completed
    }
}

function Add-VmsHardware {
    [CmdletBinding()]
    [RequiresVmsConnection()]
    param(
        [Parameter(ParameterSetName = 'FromHardwareScan', Mandatory, ValueFromPipeline)][object[]] $HardwareScan,
        [Parameter(ParameterSetName = 'Manual', Mandatory, ValueFromPipeline)][object] $RecordingServer,
        [Parameter(ParameterSetName = 'Manual', Mandatory, ValueFromPipelineByPropertyName)][Alias('Address')][uri] $HardwareAddress,
        [string] $Name,
        [Parameter(ParameterSetName = 'Manual')][int] $DriverNumber,
        [Parameter(ParameterSetName = 'Manual', ValueFromPipelineByPropertyName)][string] $HardwareDriverPath,
        [Parameter(ParameterSetName = 'Manual', Mandatory)][pscredential] $Credential,
        [switch] $SkipConfig,
        [switch] $Force
    )
    process {
        Assert-MockConnected
        $requests = if ($PSCmdlet.ParameterSetName -eq 'FromHardwareScan') {
            foreach ($s in $HardwareScan) {
                if (-not $s.HardwareScanValidated) { Write-Error "Hardware scan for $($s.HardwareAddress) was not successful: $($s.ErrorText)"; continue }
                @{ Recorder = $s.RecordingServer; Address = $s.HardwareAddress.ToString(); Password = $s.Password }
            }
        } else {
            @{ Recorder = $RecordingServer; Address = $HardwareAddress.ToString(); Password = $Credential.GetNetworkCredential().Password }
        }
        foreach ($r in $requests) {
            $existing = $script:Hardware | Where-Object Address -EQ $r.Address
            if ($existing -and -not $Force) { Write-Error "Hardware with address $($r.Address) already exists on the site."; continue }
            $hwName = if ($Name) { $Name } else { "AXIS M1065-L Network Camera ($(([uri]$r.Address).Host))" }
            $hw = Add-MockHardware -Recorder $r.Recorder -Name $hwName -Address $r.Address -Channels 1 -Enabled (-not $SkipConfig) -Password $r.Password
            $hw
        }
    }
}

function Get-VmsStorage {
    [CmdletBinding(DefaultParameterSetName = 'FromName')]
    [RequiresVmsConnection()]
    param(
        [Parameter(ValueFromPipeline, ParameterSetName = 'FromName')][object[]] $RecordingServer,
        [Parameter(ParameterSetName = 'FromName')][string] $Name = '*',
        [Parameter(Mandatory, ParameterSetName = 'FromPath')][Alias('RecordingStorage', 'Path')][string] $ItemPath
    )
    process {
        Assert-MockConnected
        if ($ItemPath) {
            $s = $script:Storage | Where-Object Path -EQ $ItemPath
            if (-not $s) { throw "Storage $ItemPath not found." }
            return $s
        }
        $items = $script:Storage
        if ($RecordingServer) { $paths = $RecordingServer | ForEach-Object Path; $items = $items | Where-Object { $paths -contains $_.ParentItemPath } }
        $items | Where-Object Name -Like $Name
    }
}

function Set-VmsDeviceStorage {
    [CmdletBinding(SupportsShouldProcess)]
    [RequiresVmsConnection()]
    param(
        [Parameter(Mandatory, ValueFromPipeline, Position = 0)][object[]] $Device,
        [Parameter(Mandatory, Position = 1)][string] $Destination,
        [switch] $PassThru
    )
    process {
        Assert-MockConnected
        foreach ($d in $Device) {
            $hw = $script:Hardware | Where-Object Path -EQ $d.ParentItemPath
            $target = $script:Storage | Where-Object { $_.ParentItemPath -eq $hw.ParentItemPath -and $_.Name -eq $Destination }
            if (-not $target) { Write-Error "Storage '$Destination' not found on the recording server of $($d.Name)."; continue }
            if ($PSCmdlet.ShouldProcess($d.Name, "Move recording storage to $Destination")) {
                $d.RecordingStorage = $target.Path
                if ($PassThru) { $d }
            }
        }
    }
}

function Get-VmsDeviceStatus {
    [CmdletBinding()]
    [RequiresVmsConnection()]
    param(
        [Parameter(Position = 0)][guid[]] $RecordingServerId,
        [Parameter(Position = 1)][ValidateSet('Camera', 'Microphone', 'Speaker', 'Metadata')][string[]] $DeviceType = 'Camera'
    )
    Assert-MockConnected
    foreach ($c in $script:Cameras) {
        $hw = $script:Hardware | Where-Object Path -EQ $c.ParentItemPath
        $rec = $script:RecordingServers | Where-Object Path -EQ $hw.ParentItemPath
        if ($RecordingServerId -and $RecordingServerId -notcontains $rec.Id) { continue }
        $s = [pscustomobject]@{
            DeviceName = $c.Name; DeviceType = 'Camera'; RecorderName = $rec.Name; RecorderId = $rec.Id; DeviceId = $c.Id
            Enabled = $c.Enabled; Started = $c.Enabled; Recording = $c.Enabled -and $c.RecordingEnabled; Motion = $false
            Error = $false; ErrorNoConnection = ($c.Enabled -and $hw.Name -eq 'Warehouse'); ErrorNotLicensed = $false; ErrorOverflow = $false
            ErrorWritingGop = $false; DbMoveInProgress = $false; DbRepairInProgress = $false; IsChange = $false; Time = [datetime]::UtcNow
        }
        $s.PSObject.TypeNames.Insert(0, 'VmsStreamDeviceStatus')
        $s
    }
}

function Get-VmsRole {
    [CmdletBinding(DefaultParameterSetName = 'ByName')]
    [RequiresVmsConnection()]
    param(
        [Parameter(ParameterSetName = 'ByName', Position = 0)][string] $Name = '*',
        [Parameter(ParameterSetName = 'ByName')][string] $RoleType,
        [Parameter(ParameterSetName = 'ById')][guid] $Id
    )
    Assert-MockConnected
    if ($PSCmdlet.ParameterSetName -eq 'ById') { return $script:Roles | Where-Object Id -EQ $Id }
    $script:Roles | Where-Object Name -Like $Name
}

function Get-VmsHardwarePassword {
    [CmdletBinding()]
    [RequiresVmsConnection()]
    param([Parameter(Mandatory, ValueFromPipeline, Position = 0)][object] $Hardware)
    process {
        Assert-MockConnected
        $script:Passwords[$Hardware.Path]
    }
}

function Get-VmsModuleConfig {
    [CmdletBinding()]
    [RequiresVmsConnection($false)]
    param()
    [pscustomobject]@{ ApplicationInsights = [pscustomobject]@{ Enabled = $true }; Mip = [pscustomobject]@{ ProxyPoolSize = 5 } }
}

function Wait-VmsTask {
    # The real command polls a server task; the mock simply waits so cancellation and timeouts can be tested.
    [CmdletBinding()]
    [RequiresVmsConnection()]
    param([Parameter(Mandatory, ValueFromPipeline, Position = 0)][string[]] $Path, [string] $Title, [switch] $Cleanup)
    process {
        Assert-MockConnected
        for ($i = 0; $i -lt 600; $i++) {
            Write-Progress -Activity ($(if ($Title) { $Title } else { 'Waiting for task' })) -PercentComplete ([math]::Min(99, $i))
            Start-Sleep -Milliseconds 100
        }
    }
}

function Select-Camera {
    [CmdletBinding()]
    [RequiresVmsConnection()]
    [RequiresInteractiveSession()]
    param([string] $Title, [switch] $SingleSelect, [switch] $AllowFolders, [switch] $AllowServers, [switch] $RemoveDuplicates, [switch] $OutputAsItem)
    throw 'Mock: Select-Camera would open a dialog.'
}

function Get-VmsBasicUser {
    [CmdletBinding()]
    [RequiresVmsConnection()]
    param([Parameter(Position = 0)][string] $Name = '*', [Parameter(Position = 1)][ValidateSet('Enabled', 'LockedOutByAdmin', 'LockedOutBySystem')][string] $Status, [switch] $External)
    Assert-MockConnected
    $script:BasicUsers | Where-Object { $_.Name -like $Name -and (-not $Status -or $_.Status -eq $Status) }
}

function New-VmsBasicUser {
    [CmdletBinding()]
    [RequiresVmsConnection()]
    param(
        [Parameter(Mandatory, Position = 0)][string] $Name,
        [Parameter(Mandatory, Position = 1)][securestring] $Password,
        [Parameter(Position = 2)][string] $Description,
        [Parameter(Position = 3)][bool] $CanChangePassword = $true,
        [Parameter(Position = 4)][bool] $ForcePasswordChange,
        [Parameter(Position = 5)][ValidateSet('Enabled', 'LockedOutByAdmin')][string] $Status = 'Enabled'
    )
    Assert-MockConnected
    if ($script:BasicUsers | Where-Object Name -EQ $Name) { throw "A basic user named '$Name' already exists." }
    $id = [guid]::NewGuid()
    $u = New-MockItem BasicUser @{ Name = $Name; Id = $id; Path = "BasicUser[$id]"; Description = $Description; Status = $Status; CanChangePassword = $CanChangePassword; ForcePasswordChange = $ForcePasswordChange; IsExternal = $false }
    $script:BasicUsers.Add($u)
    $script:BasicUserPasswords[$u.Path] = [pscredential]::new('x', $Password).GetNetworkCredential().Password
    $u
}

function Set-VmsBasicUser {
    [CmdletBinding(SupportsShouldProcess)]
    [RequiresVmsConnection()]
    param(
        [Parameter(Mandatory, ValueFromPipeline, Position = 0)][object] $BasicUser,
        [Parameter(Position = 1)][securestring] $Password,
        [Parameter(Position = 2)][string] $Description,
        [Parameter(Position = 3)][bool] $CanChangePassword,
        [Parameter(Position = 4)][bool] $ForcePasswordChange,
        [Parameter(Position = 5)][ValidateSet('Enabled', 'LockedOutByAdmin')][string] $Status,
        [switch] $PassThru
    )
    process {
        Assert-MockConnected
        if ($PSCmdlet.ShouldProcess($BasicUser.Name, 'Update basic user')) {
            foreach ($p in 'Description', 'CanChangePassword', 'ForcePasswordChange', 'Status') { if ($PSBoundParameters.ContainsKey($p)) { $BasicUser.$p = $PSBoundParameters[$p] } }
            if ($Password) { $script:BasicUserPasswords[$BasicUser.Path] = [pscredential]::new('x', $Password).GetNetworkCredential().Password }
            if ($PassThru) { $BasicUser }
        }
    }
}

function Remove-VmsBasicUser {
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    [RequiresVmsConnection()]
    param([Parameter(Mandatory, ValueFromPipeline, Position = 0)][object[]] $InputObject)
    process {
        Assert-MockConnected
        foreach ($u in $InputObject) {
            if ($PSCmdlet.ShouldProcess($u.Name, 'Remove basic user')) { $script:BasicUsers.Remove(($script:BasicUsers | Where-Object Path -EQ $u.Path)) | Out-Null }
        }
    }
}

function New-VmsRole {
    [CmdletBinding(SupportsShouldProcess)]
    [RequiresVmsConnection()]
    param(
        [Parameter(Mandatory, Position = 0)][string] $Name,
        [Parameter(Position = 1)][string] $Description,
        [switch] $AllowSmartClientLogOn, [switch] $AllowMobileClientLogOn, [switch] $AllowWebClientLogOn,
        [switch] $DualAuthorizationRequired, [switch] $MakeUsersAnonymousDuringPTZSession,
        [Parameter(Position = 2)][object] $ClientLogOnTimeProfile, [Parameter(Position = 3)][object] $DefaultTimeProfile, [Parameter(Position = 4)][object] $ClientProfile,
        [switch] $PassThru
    )
    Assert-MockConnected
    if ($PSCmdlet.ShouldProcess($Name, 'Create role')) {
        $id = [guid]::NewGuid()
        $r = New-MockItem Role @{ Name = $Name; Id = $id; Path = "Role[$id]"; RoleType = 'UserDefined'; Description = $Description }
        $script:Roles = @($script:Roles) + $r
        $script:RoleMembers[$r.Path] = New-Object System.Collections.Generic.List[object]
        if ($PassThru) { $r }
    }
}

function Get-VmsRoleMember {
    [CmdletBinding()]
    [RequiresVmsConnection()]
    param([Parameter(ValueFromPipeline, Position = 0)][object[]] $Role)
    process {
        Assert-MockConnected
        foreach ($r in $(if ($Role) { $Role } else { $script:Roles })) { if ($script:RoleMembers.ContainsKey($r.Path)) { $script:RoleMembers[$r.Path] } }
    }
}

function Add-VmsRoleMember {
    [CmdletBinding(SupportsShouldProcess, DefaultParameterSetName = 'ByAccountName')]
    [RequiresVmsConnection()]
    param(
        [Parameter(Mandatory, ValueFromPipeline, Position = 0)][object[]] $Role,
        [Parameter(Mandatory, Position = 1, ParameterSetName = 'ByAccountName')][string[]] $AccountName,
        [Parameter(Mandatory, Position = 1, ParameterSetName = 'BySid')][string[]] $Sid
    )
    process {
        Assert-MockConnected
        foreach ($r in $Role) {
            if (-not $script:RoleMembers.ContainsKey($r.Path)) { $script:RoleMembers[$r.Path] = New-Object System.Collections.Generic.List[object] }
            foreach ($a in $(if ($AccountName) { $AccountName } else { $Sid })) {
                if ($a -like 'nobody*') { Write-Error "The account '$a' could not be resolved to a SID."; continue }
                if ($PSCmdlet.ShouldProcess($r.Name, "Add member $a")) { $script:RoleMembers[$r.Path].Add((New-MockUser ($(if ($a -like '*\*') { $a } else { "CONTOSO\$a" })) ('S-1-5-21-1000-1000-1000-' + (2000 + $script:RoleMembers[$r.Path].Count)))) }
            }
        }
    }
}

function Remove-VmsRoleMember {
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High', DefaultParameterSetName = 'ByUser')]
    [RequiresVmsConnection()]
    param(
        [Parameter(Mandatory, Position = 0)][object[]] $Role,
        [Parameter(Mandatory, ValueFromPipeline, Position = 1, ParameterSetName = 'ByUser')][object[]] $User,
        [Parameter(Mandatory, Position = 1, ParameterSetName = 'BySid')][string[]] $Sid
    )
    process {
        Assert-MockConnected
        $sids = if ($User) { $User | ForEach-Object Sid } else { $Sid }
        foreach ($r in $Role) {
            foreach ($x in $sids) {
                $m = $script:RoleMembers[$r.Path] | Where-Object Sid -EQ $x
                if (-not $m) { Write-Error "SID $x is not a member of role '$($r.Name)'."; continue }
                if ($PSCmdlet.ShouldProcess($r.Name, "Remove member $($m.DisplayName)")) { $script:RoleMembers[$r.Path].Remove($m) | Out-Null }
            }
        }
    }
}

function Get-VmsRule {
    [CmdletBinding(DefaultParameterSetName = 'Name')]
    [RequiresVmsConnection()]
    [RequiresVmsVersion('20.1')]
    param([Parameter(Position = 0, ParameterSetName = 'Name')][string] $Name = '*', [Parameter(Mandatory, ParameterSetName = 'Id')][guid] $Id)
    Assert-MockConnected
    if ($PSCmdlet.ParameterSetName -eq 'Id') { return $script:Rules | Where-Object Id -EQ $Id }
    $script:Rules | Where-Object DisplayName -Like $Name
}

function Set-VmsRule {
    [CmdletBinding(SupportsShouldProcess)]
    [RequiresVmsConnection()]
    [RequiresVmsVersion('20.1')]
    param(
        [Parameter(Mandatory, ValueFromPipeline, Position = 0)][object] $Rule,
        [Parameter(Position = 1)][string] $Name,
        [Parameter(Position = 2)][bool] $Enabled,
        [Parameter(Position = 3)][hashtable] $Properties,
        [switch] $PassThru
    )
    process {
        Assert-MockConnected
        if ($PSCmdlet.ShouldProcess($Rule.DisplayName, 'Update rule')) {
            if ($PSBoundParameters.ContainsKey('Enabled')) { $Rule.Enabled = $Enabled }
            if ($PSBoundParameters.ContainsKey('Name')) { $Rule.DisplayName = $Name; $Rule.Name = $Name }
            if ($PassThru) { $Rule }
        }
    }
}

function Get-VmsViewGroup {
    [CmdletBinding(DefaultParameterSetName = 'Default')]
    [RequiresVmsConnection()]
    [RequiresVmsVersion('21.1')]
    param([Parameter(ValueFromPipeline, ParameterSetName = 'Default')][object] $Parent, [Parameter(Position = 0, ParameterSetName = 'Default')][string[]] $Name = '*', [Parameter(ParameterSetName = 'Default')][switch] $Recurse, [Parameter(Mandatory, Position = 0, ParameterSetName = 'ById')][guid] $Id)
    Assert-MockConnected
    if ($PSCmdlet.ParameterSetName -eq 'ById') { return $script:ViewGroups | Where-Object Id -EQ $Id }
    $script:ViewGroups | Where-Object { $n = $_.Name; @($Name | Where-Object { $n -like $_ }).Count -gt 0 }
}

function New-VmsViewGroup {
    [CmdletBinding()]
    [RequiresVmsConnection()]
    [RequiresVmsVersion('21.1')]
    param([Parameter(Mandatory, Position = 0)][string] $Name, [object] $Parent, [string] $Description, [switch] $Force)
    Assert-MockConnected
    $id = [guid]::NewGuid()
    $vg = New-MockItem ViewGroup @{ Name = $Name; DisplayName = $Name; Description = $Description; Id = $id; Path = "ViewGroup[$id]"; ChildCount = 0 }
    $script:ViewGroups.Add($vg)
    $vg
}

function Set-VmsViewGroup {
    [CmdletBinding(SupportsShouldProcess)]
    [RequiresVmsConnection()]
    [RequiresVmsVersion('21.1')]
    param([Parameter(Mandatory, ValueFromPipeline, Position = 0)][object] $ViewGroup, [Parameter(Position = 1)][string] $Name, [Parameter(Position = 2)][string] $Description, [switch] $PassThru)
    process {
        Assert-MockConnected
        if ($PSCmdlet.ShouldProcess($ViewGroup.DisplayName, 'Update view group')) {
            if ($PSBoundParameters.ContainsKey('Name')) { $ViewGroup.Name = $Name; $ViewGroup.DisplayName = $Name }
            if ($PSBoundParameters.ContainsKey('Description')) { $ViewGroup.Description = $Description }
            if ($PassThru) { $ViewGroup }
        }
    }
}

function Remove-VmsViewGroup {
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    [RequiresVmsConnection()]
    [RequiresVmsVersion('21.1')]
    param([Parameter(Mandatory, ValueFromPipeline, Position = 0)][object[]] $ViewGroup, [switch] $Recurse)
    process {
        Assert-MockConnected
        foreach ($vg in $ViewGroup) {
            if ($vg.ChildCount -gt 0 -and -not $Recurse) { Write-Error "View group '$($vg.Name)' is not empty. Use -Recurse to remove its contents."; continue }
            if ($PSCmdlet.ShouldProcess($vg.Name, 'Remove view group')) { $script:ViewGroups.Remove(($script:ViewGroups | Where-Object Path -EQ $vg.Path)) | Out-Null }
        }
    }
}

function Add-VmsStorage {
    [CmdletBinding(SupportsShouldProcess, DefaultParameterSetName = 'WithoutEncryption')]
    [RequiresVmsConnection()]
    param(
        [Parameter(Mandatory, ValueFromPipeline)][object] $RecordingServer,
        [Parameter(Mandatory)][string] $Name,
        [string] $Description,
        [Parameter(Mandatory)][string] $Path,
        [timespan] $Retention,
        [Parameter(Mandatory)][int] $MaximumSizeMB,
        [switch] $Default,
        [switch] $EnableSigning,
        [Parameter(Mandatory, ParameterSetName = 'WithEncryption')][string] $EncryptionMethod,
        [Parameter(Mandatory, ParameterSetName = 'WithEncryption')][securestring] $Password
    )
    process {
        Assert-MockConnected
        if ($script:Storage | Where-Object { $_.ParentItemPath -eq $RecordingServer.Path -and $_.Name -eq $Name }) { throw "Storage '$Name' already exists on $($RecordingServer.Name)." }
        if ($PSCmdlet.ShouldProcess($RecordingServer.Name, "Add storage $Name at $Path")) {
            $id = [guid]::NewGuid()
            $minutes = if ($PSBoundParameters.ContainsKey('Retention')) { [int]$Retention.TotalMinutes } else { 10080 }
            $st = New-MockItem Storage @{ Name = $Name; Id = $id; Path = "Storage[$id]"; ParentItemPath = $RecordingServer.Path; DiskPath = $Path; RetainMinutes = $minutes; MaxSize = $MaximumSizeMB; IsDefault = [bool]$Default; Description = $Description }
            $script:Storage = @($script:Storage) + $st
            $st
        }
    }
}

function Remove-VmsStorage {
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High', DefaultParameterSetName = 'ByName')]
    [RequiresVmsConnection()]
    param(
        [Parameter(Mandatory, ParameterSetName = 'ByName')][object] $RecordingServer,
        [Parameter(Mandatory, ParameterSetName = 'ByName')][string] $Name,
        [Parameter(Mandatory, ValueFromPipeline, ParameterSetName = 'ByStorage')][object] $Storage
    )
    process {
        Assert-MockConnected
        $st = if ($Storage) { $script:Storage | Where-Object Path -EQ $Storage.Path } else { $script:Storage | Where-Object { $_.ParentItemPath -eq $RecordingServer.Path -and $_.Name -eq $Name } }
        if (-not $st) { Write-Error 'Storage not found.'; return }
        # Mirrors the documented rule: only non-default storages that no device records to are removed.
        if ($st.IsDefault) { Write-Error "Storage '$($st.Name)' is the default storage and cannot be removed."; return }
        if ($script:Cameras | Where-Object RecordingStorage -EQ $st.Path) { Write-Error "Storage '$($st.Name)' is in use by one or more devices."; return }
        if ($PSCmdlet.ShouldProcess($st.Name, 'Remove storage')) { $script:Storage = @($script:Storage | Where-Object Path -NE $st.Path) }
    }
}

function New-VmsDeviceGroup {
    [CmdletBinding(DefaultParameterSetName = 'ByName')]
    [RequiresVmsConnection()]
    param(
        [Parameter(ValueFromPipeline, ParameterSetName = 'ByName')][object] $ParentGroup,
        [Parameter(Mandatory, Position = 0, ParameterSetName = 'ByName')][string[]] $Name,
        [Parameter(Mandatory, Position = 0, ParameterSetName = 'ByPath')][string[]] $Path,
        [Parameter(Position = 1)][string] $Description,
        [Parameter(Position = 2)][ValidateSet('Camera', 'Microphone', 'Speaker', 'Input', 'Output', 'Metadata')][string] $Type = 'Camera'
    )
    Assert-MockConnected
    foreach ($p in $Path) {
        # Like the real command: returns the existing group or creates the missing levels.
        if (-not $script:DeviceGroups.ContainsKey($p)) { $script:DeviceGroups[$p] = New-Object System.Collections.Generic.List[guid] }
        $g = New-MockItem CameraGroup @{ Name = ($p.TrimEnd('/') -split '/')[-1]; Path = "CameraGroup[$p]"; GroupPath = $p }
        $g
    }
}

function Add-VmsDeviceGroupMember {
    [CmdletBinding(DefaultParameterSetName = 'ByObject')]
    [RequiresVmsConnection()]
    param(
        [Parameter(ValueFromPipeline, ValueFromPipelineByPropertyName)][object] $Group,
        [Parameter(Mandatory, Position = 0, ParameterSetName = 'ByObject')][object[]] $Device,
        [Parameter(Mandatory, ValueFromPipelineByPropertyName, Position = 1, ParameterSetName = 'ById')][guid[]] $DeviceId
    )
    process {
        Assert-MockConnected
        $ids = if ($Device) { $Device | ForEach-Object Id } else { $DeviceId }
        foreach ($id in $ids) {
            if (-not ($script:Cameras | Where-Object Id -EQ $id)) { Write-Error "Failed to add device group member: Camera[$id] not found."; continue }
            if (-not $script:DeviceGroups[$Group.GroupPath].Contains($id)) { $script:DeviceGroups[$Group.GroupPath].Add($id) }
        }
    }
}

function Get-VmsDeviceGroup {
    # Only the forms the application uses: top-level groups, or every group with -Recurse. Default -Type is Camera,
    # as documented. Groups are kept as flat paths here, so intermediate levels are derived from them.
    [CmdletBinding(DefaultParameterSetName = 'ByName')]
    [RequiresVmsConnection()]
    param(
        [Parameter(ValueFromPipeline, ParameterSetName = 'ByName')][object] $ParentGroup,
        [Parameter(Position = 0, ParameterSetName = 'ByName')][string] $Name = '*',
        [Parameter(Mandatory, Position = 0, ParameterSetName = 'ByPath')][string[]] $Path,
        [Parameter(Position = 1)][ValidateSet('Camera', 'Microphone', 'Speaker', 'Input', 'Output', 'Metadata')][string] $Type = 'Camera',
        [Parameter()][switch] $Recurse
    )
    Assert-MockConnected
    if ($Type -ne 'Camera') { return }
    $all = New-Object System.Collections.Generic.SortedSet[string]
    foreach ($p in $script:DeviceGroups.Keys) {
        $parts = $p.Trim('/') -split '/'
        for ($i = 1; $i -le $parts.Count; $i++) { [void]$all.Add('/' + ($parts[0..($i - 1)] -join '/')) }
    }
    foreach ($p in $all) {
        if (-not $Recurse -and ($p.Trim('/') -split '/').Count -gt 1) { continue }
        $leaf = ($p -split '/')[-1]
        if ($leaf -notlike $Name) { continue }
        New-MockItem CameraGroup @{ Name = $leaf; Path = "CameraGroup[$p]"; GroupPath = $p }
    }
}

function Resolve-VmsDeviceGroupPath {
    # The mock simulates only -NoTypePrefix, the form the application uses.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, ValueFromPipeline, Position = 0)][object] $Group,
        [Parameter()][switch] $NoTypePrefix
    )
    process {
        if (-not $NoTypePrefix) { throw 'Mock: Resolve-VmsDeviceGroupPath without -NoTypePrefix is not simulated.' }
        $Group.GroupPath
    }
}

function Import-VmsHardware {
    # Simulates the effect only: the real command parses CSV or its Export-VmsHardware workbook format.
    [CmdletBinding(DefaultParameterSetName = 'Path')]
    [RequiresVmsConnection()]
    param(
        [Parameter(Mandatory, Position = 0, ParameterSetName = 'Path')][string[]] $Path,
        [Parameter(Mandatory, ParameterSetName = 'LiteralPath')][string[]] $LiteralPath,
        [object] $RecordingServer,
        [pscredential[]] $Credential,
        [switch] $UpdateExisting,
        [char] $Delimiter = ','
    )
    Assert-MockConnected
    foreach ($file in $(if ($LiteralPath) { $LiteralPath } else { $Path })) {
        if (-not (Test-Path -LiteralPath $file)) { Write-Error "File '$file' not found."; continue }
        Write-Progress -Activity "Importing hardware configuration from $file" -PercentComplete 50
        $rec = if ($RecordingServer) { $RecordingServer } else { $script:RecordingServers[0] }
        $address = 'http://10.9.9.9/'
        $existing = $script:Hardware | Where-Object Address -EQ $address
        if ($existing -and -not $UpdateExisting) { Write-Verbose "Skipping the hardware at $address because it is already added to $($rec.Name)."; continue }
        if ($existing) { $existing.Description = "Updated from $([io.path]::GetFileName($file))"; continue }
        $pwd = if ($Credential) { $Credential[0].GetNetworkCredential().Password } else { 'default' }
        $null = Add-MockHardware -Recorder $rec -Name ("Imported " + [io.path]::GetFileNameWithoutExtension($file)) -Address $address -Channels 2 -Enabled $true -Password $pwd
    }
}

function Invoke-MockScriptBlockProbe {
    # TEST-ONLY command (not part of MilestonePSTools): proves the engine refuses script-block parameters.
    [CmdletBinding()]
    [RequiresVmsConnection($false)]
    param([scriptblock] $Filter, [string] $Name)
    if ($Filter) { & $Filter }
    "name=$Name"
}

Set-Alias -Name Get-Camera -Value Get-VmsCamera
Reset-MockSite
Export-ModuleMember -Function * -Alias *
