param([switch]$Apply,[string]$ReportDirectory=(Join-Path $env:LOCALAPPDATA 'HonorControl\Migration'))
$ErrorActionPreference='Stop'
# Exact software paths only. DriverStore, PnP devices and audio calibration are outside this scope.
$softwareRoots=@(
    (Join-Path $env:ProgramFiles 'HONOR'),
    (Join-Path ${env:ProgramFiles(x86)} 'HONOR'),
    (Join-Path $env:ProgramData 'HONOR'),
    (Join-Path $env:WINDIR 'System32\RPC\OSD'),
    (Join-Path $env:LOCALAPPDATA 'MagicTaskbar'),
    (Join-Path $env:APPDATA 'MagicTaskbar')
) | ForEach-Object {[IO.Path]::GetFullPath($_).TrimEnd('\')}
function Test-SoftwarePath([string]$Path) {
    if(-not $Path){return $false}
    $expanded=[Environment]::ExpandEnvironmentVariables($Path).Replace('/','\').Trim('"')
    foreach($root in $softwareRoots){if($expanded.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){return $true}}
    return $false
}
function Get-SoftwareServices { @(Get-CimInstance Win32_Service | Where-Object {Test-SoftwarePath $_.PathName}) }
$serviceRecords=Get-SoftwareServices
$processRecords=@(Get-CimInstance Win32_Process | Where-Object {Test-SoftwarePath $_.ExecutablePath})
$tasks=@(Get-ScheduledTask | Where-Object { @($_.Actions | Where-Object {Test-SoftwarePath $_.Execute}).Count -gt 0 })
$uninstallRoots=@('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall','HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall','HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall')
$knownEntries=@('PC Manager','Osd','HUNTERCAMP','MagicAnimation','PCMagicAI')
$entries=@(foreach($root in $uninstallRoots){Get-ChildItem -LiteralPath $root -ErrorAction SilentlyContinue | ForEach-Object {Get-ItemProperty -LiteralPath $_.PSPath} | Where-Object {$_.PSChildName -in $knownEntries -and $_.Publisher -match 'HONOR'}})
$packages=@(Get-AppxPackage | Where-Object {$_.Name -in @('AIAssistant','AISearch','MonitorManage') -and $_.Publisher -match 'Honor'})
$report=[ordered]@{Applied=[bool]$Apply;Utc=[DateTime]::UtcNow.ToString('o');Services=@($serviceRecords | Select-Object Name,State,PathName);Processes=@($processRecords | Select-Object Name,ProcessId,ExecutablePath);Tasks=@($tasks | Select-Object TaskName,TaskPath);Applications=@($entries | Select-Object DisplayName,PSPath);Packages=@($packages | Select-Object Name,PackageFullName);Directories=@($softwareRoots | Where-Object {Test-Path -LiteralPath $_});Removed=@();Errors=@();Remaining=$null}
if($Apply){
    if(-not([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Apply requires administrator privileges.'}
    New-Item -ItemType Directory -Path $ReportDirectory -Force | Out-Null
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $ReportDirectory 'before.json') -Encoding UTF8
    foreach($task in $tasks){try{Unregister-ScheduledTask -TaskName $task.TaskName -TaskPath $task.TaskPath -Confirm:$false;$report.Removed+='Task: '+$task.TaskName}catch{$report.Errors+=$_.Exception.Message}}
    foreach($service in $serviceRecords){try{Stop-Service -Name $service.Name -Force -ErrorAction Stop;& sc.exe delete $service.Name | Out-Null;if($LASTEXITCODE -ne 0){throw 'Service removal failed: '+$service.Name};$report.Removed+='Service: '+$service.Name}catch{$report.Errors+=$_.Exception.Message}}
    foreach($process in $processRecords){try{Stop-Process -Id $process.ProcessId -Force -ErrorAction Stop}catch{if(Get-Process -Id $process.ProcessId -ErrorAction SilentlyContinue){$report.Errors+=$_.Exception.Message}}}
    $allPackages=@(Get-AppxPackage -AllUsers | Where-Object {$_.Name -in @('AIAssistant','AISearch','MonitorManage') -and $_.Publisher -match 'Honor'})
    foreach($package in $allPackages){try{Remove-AppxPackage -Package $package.PackageFullName -AllUsers -ErrorAction Stop;$report.Removed+='Appx: '+$package.Name}catch{$report.Errors+=$_.Exception.Message}}
    foreach($package in @(Get-AppxProvisionedPackage -Online | Where-Object {$_.DisplayName -in @('AIAssistant','AISearch','MonitorManage') -and $_.PackageName -match '_s7rhbyg9f8xr8$'})){
        try{Remove-AppxProvisionedPackage -Online -PackageName $package.PackageName -ErrorAction Stop | Out-Null;$report.Removed+='Provisioned Appx: '+$package.DisplayName}catch{$report.Errors+=$_.Exception.Message}
    }
    foreach($entry in $entries){try{
        $hivePath=$entry.PSPath -replace '^Microsoft.PowerShell.Core\\Registry::HKEY_LOCAL_MACHINE','HKLM' -replace '^Microsoft.PowerShell.Core\\Registry::HKEY_CURRENT_USER','HKCU'
        & reg.exe export $hivePath (Join-Path $ReportDirectory ($entry.PSChildName.Replace(' ','_')+'.reg')) /y 2>$null | Out-Null
        Remove-Item -LiteralPath $entry.PSPath -Recurse -Force
        $report.Removed+='Uninstall: '+$entry.DisplayName
    }catch{$report.Errors+=$_.Exception.Message}}
    $branches=@('AccontTips','AIAssistant','AIControl','AIServiceRouter','AIServiceRefCount','BasicService','BasicServiceSystem','BasicServiceRefCount','Collabration','CollabrativeJar','GameAgent','GameManager','HnAgentStudio','HNAIModels','HNPCAIService','HnPCInferEngine','HnRSMService','HUNTERCAMP','MagicAI','MagicAnimation','NotifControlUI','OSD','PCManager','ProcessWatcher','ProcessWatchFilter','TrustCircle','UpdateService','HonorID','YOYOInstaller','YOYOInstallFlag','NPS','BIOS','PDID','Product','Setup')
    foreach($hive in @('HKLM','HKCU')){
        $parent=$hive+':\SOFTWARE\HONOR'
        if(-not(Test-Path -LiteralPath $parent)){continue}
        & reg.exe export ($hive+'\SOFTWARE\HONOR') (Join-Path $ReportDirectory ($hive+'-HONOR.reg')) /y 2>$null | Out-Null
        foreach($branch in $branches){$key=Join-Path $parent $branch;if(Test-Path -LiteralPath $key){try{Remove-Item -LiteralPath $key -Recurse -Force;$report.Removed+='Registry: '+$key}catch{$report.Errors+=$_.Exception.Message}}}
    }
    foreach($key in @('HKCU:\Software\Microsoft\Windows\CurrentVersion\Run','HKCU:\Software\Microsoft\Windows\CurrentVersion\RunOnce','HKLM:\Software\Microsoft\Windows\CurrentVersion\Run','HKLM:\Software\Microsoft\Windows\CurrentVersion\RunOnce','HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run')){
        if(-not(Test-Path -LiteralPath $key)){continue}
        foreach($value in (Get-ItemProperty -LiteralPath $key).PSObject.Properties | Where-Object {$_.Name -notmatch '^PS' -and (Test-SoftwarePath ([string]$_.Value))}){try{Remove-ItemProperty -LiteralPath $key -Name $value.Name;$report.Removed+='Startup: '+$value.Name}catch{$report.Errors+=$_.Exception.Message}}
    }
    $shell=New-Object -ComObject WScript.Shell
    foreach($folder in @([Environment]::GetFolderPath('Programs'),[Environment]::GetFolderPath('CommonPrograms'),[Environment]::GetFolderPath('Desktop'),[Environment]::GetFolderPath('CommonDesktopDirectory'),[Environment]::GetFolderPath('Startup'),[Environment]::GetFolderPath('CommonStartup'))){
        if(-not(Test-Path -LiteralPath $folder)){continue}
        foreach($file in Get-ChildItem -LiteralPath $folder -File -Filter '*.lnk' -Recurse -ErrorAction SilentlyContinue){
            $link=$shell.CreateShortcut($file.FullName)
            if(Test-SoftwarePath $link.TargetPath){try{Remove-Item -LiteralPath $file.FullName -Force;$report.Removed+='Shortcut: '+$file.Name}catch{$report.Errors+=$_.Exception.Message}}
        }
    }
    foreach($path in $softwareRoots){
        if(-not(Test-Path -LiteralPath $path)){continue}
        # Verify the final absolute target against the exact, explicitly named list.
        $resolved=(Get-Item -LiteralPath $path -Force).FullName.TrimEnd('\')
        if($resolved -notin $softwareRoots){throw 'Unexpected cleanup target: '+$resolved}
        try{Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction Stop;$report.Removed+='Directory: '+$resolved}catch{$report.Errors+=$_.Exception.Message}
    }
    $report.Remaining=@{Services=@(Get-SoftwareServices | Select-Object Name,State);Processes=@(Get-CimInstance Win32_Process | Where-Object {Test-SoftwarePath $_.ExecutablePath} | Select-Object Name,ProcessId);Directories=@($softwareRoots | Where-Object {Test-Path -LiteralPath $_});Packages=@(Get-AppxPackage | Where-Object {$_.Name -in @('AIAssistant','AISearch','MonitorManage') -and $_.Publisher -match 'Honor'} | Select-Object Name)}
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $ReportDirectory 'result.json') -Encoding UTF8
}
$report | ConvertTo-Json -Depth 8
