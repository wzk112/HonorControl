param([string]$SourceDirectory=$PSScriptRoot)
$ErrorActionPreference='Stop'
if(-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Run this installer as administrator.'}
$source=[IO.Path]::GetFullPath($SourceDirectory).TrimEnd('\')
$install=Join-Path $env:ProgramFiles 'HonorControl'
foreach($required in @('HonorControl.exe','HonorControl.Broker.exe','Backend\HonorStandalone.exe','Backend\HonorColorWatcher.exe')){if(-not (Test-Path -LiteralPath (Join-Path $source $required) -PathType Leaf)){throw ('Missing package file: '+$required)}}
if($source -eq $install){throw 'Use a separate extracted package as the installation source.'}
$userSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$localState=Join-Path $env:LOCALAPPDATA 'HonorControl'
New-Item -ItemType Directory -Path $install,$localState -Force|Out-Null
$tasks = @(Get-ScheduledTask -TaskName 'HonorControl.Agent','HonorControl.UI' -ErrorAction SilentlyContinue)
foreach ($task in $tasks) {
    Disable-ScheduledTask -InputObject $task | Out-Null
    Stop-ScheduledTask -InputObject $task -ErrorAction SilentlyContinue
}
try {
    foreach($process in @(Get-CimInstance Win32_Process -Filter "Name='HonorControl.exe' OR Name='HonorControl.Broker.exe' OR Name='HonorColorWatcher.exe'")){
        if($process.ExecutablePath -and ($process.ExecutablePath.StartsWith($install+'\',[StringComparison]::OrdinalIgnoreCase) -or $process.ExecutablePath.StartsWith($source+'\',[StringComparison]::OrdinalIgnoreCase))){
            $running = Get-Process -Id $process.ProcessId -ErrorAction SilentlyContinue
            if ($running) {
                Stop-Process -InputObject $running -Force -ErrorAction SilentlyContinue
                if (-not $running.WaitForExit(10000)) { throw "Process did not exit: $($process.Name)" }
            }
        }
    }
    for ($attempt = 0; ; $attempt++) {
        try { Copy-Item -Path (Join-Path $source '*') -Destination $install -Recurse -Force; break }
        catch [System.IO.IOException] { if ($attempt -ge 9) { throw }; Start-Sleep -Milliseconds 400 }
    }
} catch {
    foreach ($task in $tasks) {
        Enable-ScheduledTask -TaskName $task.TaskName | Out-Null
        Start-ScheduledTask -TaskName $task.TaskName -ErrorAction SilentlyContinue
    }
    throw
}
$deviceState=Join-Path $localState 'DeviceState'
New-Item -ItemType Directory $deviceState -Force|Out-Null
foreach($name in @('HonorNative.icc','HonorsRGB.icc','HonorP3.icc','sRGB.lut','P3.lut')){
    $target=Join-Path $install ('Backend\'+$name)
    $cached=Join-Path $localState ('Calibration\'+$name)
    if(-not(Test-Path -LiteralPath $target) -and (Test-Path -LiteralPath $cached)){Copy-Item -LiteralPath $cached -Destination $target -Force}
}
if(Test-Path -LiteralPath (Join-Path $source 'Backend\state')){Get-ChildItem -LiteralPath (Join-Path $source 'Backend\state') -File|ForEach-Object{$target=Join-Path $deviceState $_.Name;if(-not(Test-Path -LiteralPath $target)){Copy-Item -LiteralPath $_.FullName -Destination $target}}}
$hasCalibration=@('HonorNative.icc','HonorsRGB.icc','HonorP3.icc','sRGB.lut','P3.lut').Where({-not(Test-Path -LiteralPath (Join-Path $install ('Backend\'+$_)))}).Count -eq 0
if($hasCalibration -and -not(Test-Path -LiteralPath (Join-Path $deviceState 'last-color-mode.txt'))){
    $status=& (Join-Path $install 'Backend\HonorStandalone.exe') status|ConvertFrom-Json
    if($status.ClosestFactoryMode -in @('Native','sRGB','P3')){[IO.File]::WriteAllText((Join-Path $deviceState 'last-color-mode.txt'),$status.ClosestFactoryMode.ToLowerInvariant())}
}
Remove-Item -LiteralPath (Join-Path $localState 'pause-resident.flag') -ErrorAction SilentlyContinue
$watchPreference=Join-Path $localState 'color-watch-enabled.txt'
if(-not(Test-Path -LiteralPath $watchPreference)){[IO.File]::WriteAllText($watchPreference,$(if($hasCalibration){'1'}else{'0'}))}
Remove-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name HonorStandaloneColor -ErrorAction SilentlyContinue
if($hasCalibration){
    & (Join-Path $install 'Backend\HonorStandalone.exe') install-profiles|Out-Null
    if($LASTEXITCODE -ne 0){throw 'ICC profile installation failed.'}
}
$trigger=New-ScheduledTaskTrigger -AtLogOn -User $userSid
$settings=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew
$agentPrincipal=New-ScheduledTaskPrincipal -UserId $userSid -LogonType Interactive -RunLevel Highest
$uiPrincipal=New-ScheduledTaskPrincipal -UserId $userSid -LogonType Interactive -RunLevel Limited
$agentAction=New-ScheduledTaskAction -Execute (Join-Path $install 'HonorControl.Broker.exe') -Argument '--resident' -WorkingDirectory $install
$uiAction=New-ScheduledTaskAction -Execute (Join-Path $install 'HonorControl.exe') -Argument '--background' -WorkingDirectory $install
$null=Register-ScheduledTask -TaskName 'HonorControl.Agent' -Action $agentAction -Trigger $trigger -Principal $agentPrincipal -Settings $settings -Description 'Honor Control fixed hardware commands and UI recovery.' -Force
$null=Register-ScheduledTask -TaskName 'HonorControl.UI' -Action $uiAction -Trigger $trigger -Principal $uiPrincipal -Settings $settings -Description 'Honor Control tray, color recovery and passive mode overlay.' -Force
@{Version='0.14.4';UserSid=$userSid;InstalledUtc=[DateTime]::UtcNow.ToString('o');AutoStart=$true;InstallDirectory=$install}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $install 'installed.json') -Encoding UTF8
$shell=New-Object -ComObject WScript.Shell
$startMenu=Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Honor Control.lnk'
$shortcut=$shell.CreateShortcut($startMenu);$shortcut.TargetPath=Join-Path $install 'HonorControl.exe';$shortcut.WorkingDirectory=$install;$shortcut.IconLocation=(Join-Path $install 'Assets\HonorControl.ico')+',0';$shortcut.Save()
$uninstallKey='HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\HonorControl'
New-Item -Path $uninstallKey -Force|Out-Null
foreach($pair in @(@('DisplayName','Honor Control'),@('DisplayVersion','0.14.4'),@('Publisher','wzk112'),@('InstallLocation',$install),@('DisplayIcon',(Join-Path $install 'Assets\HonorControl.ico')))){New-ItemProperty -LiteralPath $uninstallKey -Name $pair[0] -Value $pair[1] -PropertyType String -Force|Out-Null}
if(Test-Path -LiteralPath (Join-Path $install 'HonorControl.Uninstall.exe')){New-ItemProperty -LiteralPath $uninstallKey -Name 'UninstallString' -Value ('"'+(Join-Path $install 'HonorControl.Uninstall.exe')+'"') -PropertyType String -Force | Out-Null}
Start-ScheduledTask -TaskName 'HonorControl.Agent'
Start-ScheduledTask -TaskName 'HonorControl.UI'
@{Installed=$true;Path=$install;UserSid=$userSid;TaskNames=@('HonorControl.Agent','HonorControl.UI')}|ConvertTo-Json -Depth 4
