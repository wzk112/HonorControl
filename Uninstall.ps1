$ErrorActionPreference='Stop'
if(-not([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Administrator privileges are required.'}
$install=[IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'HonorControl')).TrimEnd('\')
if([IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') -ne $install){throw 'Run the installed uninstaller from Program Files.'}
$state=Join-Path $env:LOCALAPPDATA 'HonorControl'
New-Item -ItemType Directory -Path $state -Force | Out-Null
$calibration=Join-Path $state 'Calibration'
New-Item -ItemType Directory -Path $calibration -Force | Out-Null
foreach($name in @('HonorNative.icc','HonorsRGB.icc','HonorP3.icc','sRGB.lut','P3.lut')){$source=Join-Path $install ('Backend\'+$name);if(Test-Path -LiteralPath $source){Copy-Item -LiteralPath $source -Destination $calibration -Force}}
[IO.File]::WriteAllText((Join-Path $state 'pause-resident.flag'),'Paused for uninstall.')
foreach($task in @(Get-ScheduledTask -TaskName 'HonorControl.UI','HonorControl.Agent' -ErrorAction SilentlyContinue)){Disable-ScheduledTask -InputObject $task | Out-Null}
& (Join-Path $install 'HonorControl.exe') --exit
$end=[DateTime]::UtcNow.AddSeconds(25)
do {
    $ui=@(Get-CimInstance Win32_Process -Filter "Name='HonorControl.exe'" | Where-Object {$_.ExecutablePath -eq (Join-Path $install 'HonorControl.exe')})
    if(-not $ui.Count){break}
    Start-Sleep -Milliseconds 300
} while([DateTime]::UtcNow -lt $end)
if($ui.Count){throw 'Honor Control has not finished restoring its UI policies. Close it from the tray and try again.'}
$journals=@('DeviceState\dtt-restore.json','DeviceState\gpu-clock-restore.json','gpu-restore.flag','auto-performance-restore.json')
$end=[DateTime]::UtcNow.AddSeconds(30)
do {
    $pending=@($journals | Where-Object {Test-Path -LiteralPath (Join-Path $state $_)})
    if(-not $pending.Count){break}
    Start-Sleep -Milliseconds 500
} while([DateTime]::UtcNow -lt $end)
if($pending.Count){throw ('Device restoration is incomplete. Reconnect AC power and retry. Retained records: '+($pending -join ', '))}
foreach($task in @(Get-ScheduledTask -TaskName 'HonorControl.UI','HonorControl.Agent' -ErrorAction SilentlyContinue)){Stop-ScheduledTask -InputObject $task -ErrorAction SilentlyContinue;Unregister-ScheduledTask -InputObject $task -Confirm:$false}
foreach($process in @(Get-CimInstance Win32_Process | Where-Object {$_.ExecutablePath -and $_.ExecutablePath.StartsWith($install+'\',[StringComparison]::OrdinalIgnoreCase)})){
    $running=Get-Process -Id $process.ProcessId -ErrorAction SilentlyContinue
    if($running){Stop-Process -InputObject $running -Force -ErrorAction SilentlyContinue;if(-not $running.WaitForExit(10000)){throw 'A running component did not exit.'}}
}
Remove-Item -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\HonorControl' -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Honor Control.lnk') -Force -ErrorAction SilentlyContinue
$resolved=(Get-Item -LiteralPath $install -Force).FullName.TrimEnd('\')
if($resolved -ne [IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'HonorControl')).TrimEnd('\')){throw 'Unexpected uninstall target.'}
Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction Stop
Write-Output 'Honor Control removed. Personal settings, logs, backups and hardware drivers were retained.'
