[CmdletBinding()]
param(
    [ValidateSet('Show','Priority','Limit','Remove','Menu')][string]$Action='Show',
    [string]$AppPath,
    [ValidateRange(0.1,100000)][double]$RateMbps=8,
    [ValidateRange(0,63)][int]$Dscp=46
)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
Import-Module NetQos -ErrorAction Stop
function Policy-Name([string]$path){
    $hash=[Security.Cryptography.SHA256]::Create()
    try{$bytes=$hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($path.ToLowerInvariant()));return 'HonorStandalone-'+([BitConverter]::ToString($bytes,0,8)-replace '-','')}
    finally{$hash.Dispose()}
}
function Apply-Action([string]$operation,[string]$path,[double]$rate,[int]$mark){
    if($operation -eq 'Show'){
        return @(Get-NetQosPolicy -PolicyStore ActiveStore -ErrorAction Stop|Where-Object{$_.Name -like 'HonorStandalone-*'}|Select-Object Name,AppPathNameMatchCondition,DSCPAction,ThrottleRateAction)
    }
    if(-not [IO.Path]::IsPathRooted($path) -or [IO.Path]::GetExtension($path) -ne '.exe'){throw 'Enter a complete .exe path.'}
    $resolved=[IO.Path]::GetFullPath($path);$name=Policy-Name $resolved
    $previous=Get-NetQosPolicy -Name $name -PolicyStore ActiveStore -ErrorAction SilentlyContinue
    if($operation -eq 'Remove'){
        if($previous){Remove-NetQosPolicy -Name $name -PolicyStore ActiveStore -Confirm:$false -ErrorAction Stop}
        return [pscustomobject]@{Action='Remove';Name=$name;Removed=(-not [bool](Get-NetQosPolicy -Name $name -PolicyStore ActiveStore -ErrorAction SilentlyContinue))}
    }
    if(-not (Test-Path -LiteralPath $resolved -PathType Leaf)){throw 'Application executable was not found.'}
    $settings=@{Name=$name;AppPathNameMatchCondition=$resolved;PolicyStore='ActiveStore';ErrorAction='Stop'}
    if($operation -eq 'Priority'){
        $settings.DSCPAction=$mark
        if($previous -and [UInt64]$previous.ThrottleRateAction -gt 0){$settings.ThrottleRateActionBitsPerSecond=[UInt64]$previous.ThrottleRateAction}
    }elseif($operation -eq 'Limit'){
        $settings.ThrottleRateActionBitsPerSecond=[UInt64]($rate*1000000)
        if($previous -and $previous.DSCPAction -ge 0){$settings.DSCPAction=[sbyte]$previous.DSCPAction}
    }else{throw 'Unknown network operation.'}
    try{
        if($previous){Remove-NetQosPolicy -Name $name -PolicyStore ActiveStore -Confirm:$false -ErrorAction Stop}
        $null=New-NetQosPolicy @settings
        $current=Get-NetQosPolicy -Name $name -PolicyStore ActiveStore -ErrorAction Stop
        if($operation -eq 'Priority' -and $current.DSCPAction -ne $mark){throw 'DSCP rule readback mismatch.'}
        if($operation -eq 'Limit' -and [UInt64]$current.ThrottleRateAction -ne $settings.ThrottleRateActionBitsPerSecond){throw 'Rate-limit rule readback mismatch.'}
        return [pscustomobject]@{Action=$operation;VerifiedPolicyReadback=$true;Policy=($current|Select-Object Name,AppPathNameMatchCondition,DSCPAction,ThrottleRateAction);Scope='Outbound traffic; ActiveStore rules clear on restart. DSCP priority depends on the network equipment.'}
    }catch{
        $failure=$_.Exception
        if(Get-NetQosPolicy -Name $name -PolicyStore ActiveStore -ErrorAction SilentlyContinue){Remove-NetQosPolicy -Name $name -PolicyStore ActiveStore -Confirm:$false -ErrorAction SilentlyContinue}
        if($previous){
            $restore=@{Name=$name;AppPathNameMatchCondition=$resolved;PolicyStore='ActiveStore';ErrorAction='Stop'}
            if($previous.DSCPAction -ge 0){$restore.DSCPAction=[sbyte]$previous.DSCPAction}
            if([UInt64]$previous.ThrottleRateAction -gt 0){$restore.ThrottleRateActionBitsPerSecond=[UInt64]$previous.ThrottleRateAction}
            $null=New-NetQosPolicy @restore
        }
        throw $failure
    }
}
if($Action -eq 'Menu'){
    while($true){
        Write-Host "`nIndependent Windows network QoS (outbound traffic)"
        Write-Host '1 List rules   2 Priority mark   3 Upload limit   4 Remove app rule   Q Exit'
        $choice=(Read-Host 'Choose').Trim().ToLowerInvariant()
        if($choice -eq 'q'){break}
        try{
            switch($choice){
                '1' {Apply-Action Show '' 0 0|ConvertTo-Json -Depth 6}
                '2' {$path=(Read-Host 'Full app .exe path').Trim('"');Apply-Action Priority $path 0 46|ConvertTo-Json -Depth 6}
                '3' {$path=(Read-Host 'Full app .exe path').Trim('"');$rate=[double](Read-Host 'Upload limit in Mbps');if($rate -lt .1 -or $rate -gt 100000){throw 'Invalid rate.'};Apply-Action Limit $path $rate 46|ConvertTo-Json -Depth 6}
                '4' {$path=(Read-Host 'Full app .exe path').Trim('"');Apply-Action Remove $path 0 0|ConvertTo-Json -Depth 6}
            }
        }catch{Write-Host $_.Exception.Message}
    }
}else{Apply-Action $Action $AppPath $RateMbps $Dscp|ConvertTo-Json -Depth 6}
