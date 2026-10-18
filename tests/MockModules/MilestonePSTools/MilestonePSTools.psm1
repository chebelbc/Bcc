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
        (New-MockItem Role @{ Name = 'Administrators'; Id = [guid]'44444444-0000-0000-0000-000000000001'; RoleType = 'Adminstrative'; Description = 'Built-in' }),
        (New-MockItem Role @{ Name = 'Operators'; Id = [guid]'44444444-0000-0000-0000-000000000002'; RoleType = 'UserDefined'; Description = '' })
    )
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
    param([switch] $ListAvailable, [Parameter(Position = 0)][string] $Name)
    Assert-MockConnected
    $site = [pscustomobject]@{ Name = 'Mock Site'; FQID = 'ServerId: mgmt.example.local' }
    $site.PSObject.TypeNames.Insert(0, 'VideoOS.Platform.Item')
    $site
}

function Get-VmsManagementServer {
    [CmdletBinding()]
    [RequiresVmsConnection()]
    param()
    Assert-MockConnected
    New-MockItem ManagementServer @{ Name = 'Mock Site'; ComputerName = 'MGMT01'; Version = '25.2.4815.1'; Id = [guid]'22222222-0000-0000-0000-000000000001'; Description = '' }
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
