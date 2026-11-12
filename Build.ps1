param([string]$OutputDirectory=(Join-Path $PSScriptRoot 'artifacts\HonorControl'),[string]$CalibrationDirectory=(Join-Path $PSScriptRoot 'BackendSource'),[switch]$WithoutCalibration)
$ErrorActionPreference='Stop'
$outputPath=[IO.Path]::GetFullPath($OutputDirectory)
$runningApps=@(Get-Process HonorControl -ErrorAction SilentlyContinue|Where-Object{try{[string]::Equals($_.Path,(Join-Path $outputPath 'HonorControl.exe'),[StringComparison]::OrdinalIgnoreCase)}catch{$false}})
if($runningApps.Count -gt 0){throw 'Close the application in the selected output directory before building, or choose a new output directory.'}
$backendPath=Join-Path $outputPath 'Backend'
$brokerStage=Join-Path $PSScriptRoot 'artifacts\broker'
New-Item -ItemType Directory -Path $backendPath -Force|Out-Null
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources=@('HonorStandalone.cs','HonorFirmware.cs','WindowsColor.cs','ColorWatcher.cs','DisplayEffects.cs','BrightnessRhythm.cs','DttPolicy.cs','NvidiaBoost.cs')|ForEach-Object{Join-Path $PSScriptRoot ('BackendSource\'+$_)}
$sources+=Join-Path $PSScriptRoot 'Shared\DiagnosticLog.cs'
& $compiler /nologo /platform:x64 /optimize+ /r:System.Core.dll /r:System.Management.dll /r:System.Web.Extensions.dll ('/out:'+(Join-Path $backendPath 'HonorStandalone.exe')) @sources
if($LASTEXITCODE -ne 0){throw 'Backend compilation failed.'}
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /r:System.Core.dll /r:System.Management.dll ('/out:'+(Join-Path $backendPath 'HonorColorWatcher.exe')) (Join-Path $PSScriptRoot 'BackendSource\ColorWatcher.cs') (Join-Path $PSScriptRoot 'BackendSource\ColorWatcherHost.cs') (Join-Path $PSScriptRoot 'BackendSource\BrightnessRhythm.cs') (Join-Path $PSScriptRoot 'Shared\DiagnosticLog.cs')
if($LASTEXITCODE -ne 0){throw 'Color watcher compilation failed.'}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'BackendSource\DraPolicies.tsv') -Destination $backendPath -Force
if(-not $WithoutCalibration){
    foreach($pattern in @('*.icc','*.lut','calibration-info.json')){Get-ChildItem -Path (Join-Path $CalibrationDirectory $pattern) -File -ErrorAction SilentlyContinue | Copy-Item -Destination $backendPath -Force}
}
& dotnet publish (Join-Path $PSScriptRoot 'Broker\HonorControl.Broker.csproj') -c Release -o $brokerStage -p:DebugType=None -p:DebugSymbols=false ('-p:PathMap='+$PSScriptRoot+'=/_/') --verbosity minimal
if($LASTEXITCODE -ne 0){throw 'Broker publish failed.'}
& dotnet publish (Join-Path $PSScriptRoot 'App\HonorControl.csproj') -c Release -o $outputPath -p:DebugType=None -p:DebugSymbols=false ('-p:PathMap='+$PSScriptRoot+'=/_/') --verbosity minimal
if($LASTEXITCODE -ne 0){throw 'WinUI publish failed.'}
Copy-Item -LiteralPath (Join-Path $brokerStage 'HonorControl.Broker.exe') -Destination $outputPath -Force
Get-ChildItem -LiteralPath $outputPath -File -Filter '*.pdb' | ForEach-Object {Remove-Item -LiteralPath $_.FullName -Force}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Broker\NetworkPolicy.ps1') -Destination $outputPath -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $outputPath -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install.ps1') -Destination $outputPath -Force
foreach($file in @('README.en-US.md','INSTALL.zh-CN.md','INSTALL.en-US.md','CHANGELOG.md','LICENSE','THIRD-PARTY-NOTICES.md','Clean-HonorSoftware.ps1','Import-Calibration.ps1','Uninstall.ps1')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $outputPath -Force}
if(Test-Path -LiteralPath (Join-Path $PSScriptRoot 'docs')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs') -Destination $outputPath -Recurse -Force}
$licenses=Join-Path $outputPath 'Licenses'
New-Item -ItemType Directory -Path $licenses -Force | Out-Null
$dotnetRoot=Split-Path (Get-Command dotnet).Source
foreach($file in @('LICENSE.txt','ThirdPartyNotices.txt')){Copy-Item -LiteralPath (Join-Path $dotnetRoot $file) -Destination (Join-Path $licenses ('dotnet-'+$file)) -Force}
$appSdk=Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windowsappsdk\1.8.260921001'
foreach($file in @('license.txt','NOTICE.txt')){Copy-Item -LiteralPath (Join-Path $appSdk $file) -Destination (Join-Path $licenses ('WindowsAppSDK-'+$file)) -Force}
Write-Host ('Ready: '+(Join-Path $outputPath 'HonorControl.exe'))
