param([Parameter(Mandatory=$true)][string]$PackageDirectory,[string]$OutputDirectory=(Join-Path $PSScriptRoot 'artifacts\setup'))
$ErrorActionPreference='Stop'
$package=[IO.Path]::GetFullPath($PackageDirectory)
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(-not(Test-Path -LiteralPath (Join-Path $package 'HonorControl.exe'))){throw 'Build the application package first.'}
New-Item -ItemType Directory -Path $output -Force | Out-Null
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$source=Join-Path $PSScriptRoot 'SetupSource\Setup.cs'
$manifest=Join-Path $PSScriptRoot 'SetupSource\setup.manifest'
$license=Join-Path $PSScriptRoot 'LICENSE'
$icon=Join-Path $PSScriptRoot 'App\Assets\HonorControl.ico'
$common=@('/nologo','/codepage:65001','/target:winexe','/platform:x64','/optimize+','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll',('/win32manifest:'+$manifest),('/win32icon:'+$icon),('/resource:'+$license+',License'))
& $compiler @common /define:UNINSTALL ('/out:'+(Join-Path $package 'HonorControl.Uninstall.exe')) $source
if($LASTEXITCODE -ne 0){throw 'Uninstaller compilation failed.'}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Uninstall.ps1') -Destination $package -Force
$zip=Join-Path $output 'package.zip'
if(Test-Path -LiteralPath $zip){Remove-Item -LiteralPath $zip -Force}
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($package,$zip,[IO.Compression.CompressionLevel]::Optimal,$false)
$hash=Join-Path $output 'package.sha256'
[IO.File]::WriteAllText($hash,(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash)
$target=Join-Path $output 'HonorControl-0.14.4-Setup-x64.exe'
& $compiler @common ('/resource:'+$zip+',Package') ('/resource:'+$hash+',PackageHash') ('/out:'+$target) $source
if($LASTEXITCODE -ne 0){throw 'Installer compilation failed.'}
$checksum=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(($target+'.sha256'),$checksum+'  '+[IO.Path]::GetFileName($target)+[Environment]::NewLine)
Write-Host ('Installer ready: '+$target)
