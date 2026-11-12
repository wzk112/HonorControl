param([Parameter(Mandatory=$true)][string]$Payload)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
Import-Module NetQos -ErrorAction Stop
$json=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Payload))
$items=ConvertFrom-Json -InputObject $json
if($items.Count -gt 48){throw 'Too many application policies.'}
$wanted=@{}
foreach($item in $items){
    if($null -eq $item){continue}
    $path=[IO.Path]::GetFullPath([string]$item.Path)
    if(-not [IO.Path]::IsPathRooted($path) -or [IO.Path]::GetExtension($path) -ne '.exe' -or -not (Test-Path -LiteralPath $path -PathType Leaf)){throw 'Invalid application path.'}
    $hash=[Security.Cryptography.SHA256]::Create()
    try{$bytes=$hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($path.ToLowerInvariant()));$name='HonorControlNet-'+([BitConverter]::ToString($bytes,0,12)-replace '-','')}finally{$hash.Dispose()}
    $dscp=[int]$item.Dscp;$rate=[double]$item.UploadMbps
    if($dscp -notin @(0,34,46) -or [double]::IsNaN($rate) -or [double]::IsInfinity($rate) -or $rate -lt 0 -or $rate -gt 100000){throw 'Invalid network policy.'}
    $wanted[$name]=[pscustomobject]@{Path=$path;Dscp=$dscp;Rate=$rate}
}
$existing=@(Get-NetQosPolicy -PolicyStore ActiveStore -ErrorAction Stop|Where-Object{$_.Name -like 'HonorControlNet-*'})
foreach($policy in $existing){if(-not $wanted.ContainsKey($policy.Name)){Remove-NetQosPolicy -Name $policy.Name -PolicyStore ActiveStore -Confirm:$false -ErrorAction Stop}}
foreach($name in $wanted.Keys){
    $item=$wanted[$name];$previous=Get-NetQosPolicy -Name $name -PolicyStore ActiveStore -ErrorAction SilentlyContinue
    $bits=[UInt64]($item.Rate*1000000)
    if($previous -and $previous.DSCPAction -eq $item.Dscp -and [UInt64]$previous.ThrottleRateAction -eq $bits){continue}
    if($previous){Remove-NetQosPolicy -Name $name -PolicyStore ActiveStore -Confirm:$false -ErrorAction Stop}
    $settings=@{Name=$name;AppPathNameMatchCondition=$item.Path;DSCPAction=[sbyte]$item.Dscp;PolicyStore='ActiveStore';ErrorAction='Stop'}
    if($bits -gt 0){$settings.ThrottleRateActionBitsPerSecond=$bits}
    $null=New-NetQosPolicy @settings
    $check=Get-NetQosPolicy -Name $name -PolicyStore ActiveStore -ErrorAction Stop
    if($check.DSCPAction -ne $item.Dscp -or [UInt64]$check.ThrottleRateAction -ne $bits){throw 'Network policy verification failed.'}
}
@{Verified=$true;Count=$wanted.Count}|ConvertTo-Json -Compress
